using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Backup
{
    public static class BackupService
    {
        public static Task DeleteBackupAsync(MetadataService metadata, long backupId)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (backupId <= 0) throw new ArgumentOutOfRangeException(nameof(backupId));

            metadata.InitializeDatabase();

            using var sqlite = new SqliteConnection(metadata.ConnectionString);
            sqlite.Open();

            using (var pragma = sqlite.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            using var tx = sqlite.BeginTransaction();
            using (var del = sqlite.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM Backups WHERE BackupID = $id";
                del.Parameters.AddWithValue("$id", backupId);
                del.ExecuteNonQuery();
            }

            var orphanPaths = new List<(string hash, string path)>();
            using (var q = sqlite.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = @"SELECT bi.block_hash, bi.storage_path
                                   FROM BlockIndex bi
                                   WHERE NOT EXISTS (
                                       SELECT 1 FROM SnapshotBlocks sb WHERE sb.block_hash = bi.block_hash
                                   )";
                using var rdr = q.ExecuteReader();
                while (rdr.Read())
                {
                    orphanPaths.Add((rdr.GetString(0), rdr.GetString(1)));
                }
            }

            try
            {
                var baseDir = Path.GetDirectoryName(metadata.DatabasePath) ?? AppContext.BaseDirectory;
                foreach (var (_, rel) in orphanPaths)
                {
                    var abs = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(abs))
                    {
                        try { File.Delete(abs); } catch { }
                    }
                }
            }
            catch { }

            using (var cleanupIdx = sqlite.CreateCommand())
            {
                cleanupIdx.Transaction = tx;
                cleanupIdx.CommandText = @"DELETE FROM BlockIndex
                                           WHERE NOT EXISTS (
                                               SELECT 1 FROM SnapshotBlocks sb WHERE sb.block_hash = BlockIndex.block_hash
                                           )";
                cleanupIdx.ExecuteNonQuery();
            }

            tx.Commit();
            return Task.CompletedTask;
        }

        private static string ToHex(ReadOnlySpan<byte> bytes)
        {
            char[] c = new char[bytes.Length * 2];
            int b;
            for (int i = 0; i < bytes.Length; i++)
            {
                b = bytes[i] >> 4;
                c[i * 2] = (char)(55 + b + (((b - 10) >> 31) & -7));
                b = bytes[i] & 0xF;
                c[i * 2 + 1] = (char)(55 + b + (((b - 10) >> 31) & -7));
            }
            for (int i = 0; i < c.Length; i++)
            {
                if (c[i] >= 'A' && c[i] <= 'F') c[i] = (char)(c[i] + 32);
            }
            return new string(c);
        }

        public static async Task<long> ExecuteBackupViaBakAsync(MetadataService metadata, Microsoft.Data.SqlClient.SqlConnection masterConnection, string databaseName, string backupType = "Full", IProgress<int>? progress = null)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (masterConnection == null) throw new ArgumentNullException(nameof(masterConnection));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Value is required", nameof(databaseName));

            metadata.InitializeDatabase();
            if (masterConnection.State != System.Data.ConnectionState.Open)
                await masterConnection.OpenAsync().ConfigureAwait(false);

            string serverBakDir;
            await using (var p = masterConnection.CreateCommand())
            {
                p.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
                var v = await p.ExecuteScalarAsync().ConfigureAwait(false);
                var dirVal = Convert.ToString(v) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dirVal)) serverBakDir = "/var/opt/mssql/backup"; else serverBakDir = dirVal.Contains(":") ? dirVal : "/var/opt/mssql/backup";
            }

            var bakName = $"{databaseName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_Full.bak";
            var sepStr = serverBakDir.Contains("/") ? "/" : "\\";
            var serverBakPath = (serverBakDir.EndsWith("/") || serverBakDir.EndsWith("\\")) ? serverBakDir + bakName : serverBakDir + sepStr + bakName;

            await using (var cmd = masterConnection.CreateCommand())
            {
                cmd.CommandText = $"BACKUP DATABASE [{databaseName}] TO DISK = @p WITH NOFORMAT, INIT, NAME = @n, SKIP, NOREWIND, NOUNLOAD, STATS = 5, NO_COMPRESSION";
                cmd.Parameters.AddWithValue("@p", serverBakPath);
                cmd.Parameters.AddWithValue("@n", $"Full backup of {databaseName}");
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            long backupIdOut;
            await using (var bulkCmd = masterConnection.CreateCommand())
            {
                bulkCmd.CommandText =
                    "DECLARE @p nvarchar(4000) = @path; " +
                    "DECLARE @sql nvarchar(max) = N'SELECT BulkColumn FROM OPENROWSET(BULK ' + QUOTENAME(@p, '''') + ', SINGLE_BLOB) AS x'; " +
                    "EXEC (@sql);";
                bulkCmd.Parameters.AddWithValue("@path", serverBakPath);

                await using var reader = await bulkCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess).ConfigureAwait(false);

                await using var sqlite = new SqliteConnection(metadata.ConnectionString);
                await sqlite.OpenAsync().ConfigureAwait(false);
                await using (var pragma = sqlite.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys = ON;";
                    await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                using var tx = sqlite.BeginTransaction();

                await using (var insertBackup = sqlite.CreateCommand())
                {
                    insertBackup.Transaction = tx;
                    insertBackup.CommandText = "INSERT INTO Backups (Timestamp, Type, TableName) VALUES ($ts, $type, $db); SELECT last_insert_rowid();";
                    insertBackup.Parameters.AddWithValue("$ts", DateTime.UtcNow);
                    insertBackup.Parameters.AddWithValue("$type", backupType);
                    insertBackup.Parameters.AddWithValue("$db", databaseName);
                    var obj = await insertBackup.ExecuteScalarAsync().ConfigureAwait(false);
                    backupIdOut = Convert.ToInt64(obj);
                }

                await using var existsIdx = sqlite.CreateCommand();
                existsIdx.Transaction = tx;
                existsIdx.CommandText = "SELECT 1 FROM BlockIndex WHERE block_hash = $h LIMIT 1";
                var pExistsH = existsIdx.Parameters.Add("$h", SqliteType.Text);

                await using var insertIdx = sqlite.CreateCommand();
                insertIdx.Transaction = tx;
                insertIdx.CommandText = "INSERT INTO BlockIndex (block_hash, storage_path, block_size) VALUES ($h, $p, $s)";
                var pInsH = insertIdx.Parameters.Add("$h", SqliteType.Text);
                var pInsP = insertIdx.Parameters.Add("$p", SqliteType.Text);
                var pInsS = insertIdx.Parameters.Add("$s", SqliteType.Integer);

                await using var insertSnap = sqlite.CreateCommand();
                insertSnap.Transaction = tx;
                insertSnap.CommandText = "INSERT INTO SnapshotBlocks (BackupID, block_hash, BlockOrder) VALUES ($bid, $h, $o)";
                var pBid = insertSnap.Parameters.Add("$bid", SqliteType.Integer);
                var pH = insertSnap.Parameters.Add("$h", SqliteType.Text);
                var pO = insertSnap.Parameters.Add("$o", SqliteType.Integer);
                pBid.Value = backupIdOut;

                var baseDir = Path.GetDirectoryName(metadata.DatabasePath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(Path.Combine(baseDir, "blocks"));

                int order = 1;
                int processed = 0;
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    using var bakStream = reader.GetStream(0);
                      await foreach (var chunk in ContentDefinedChunkStream(bakStream))
                      {
                        var hash = ToHex(SHA256.HashData(chunk));
                        pExistsH.Value = hash;
                        var exists = existsIdx.ExecuteScalar() != null;
                        var rel = Path.Combine("blocks", hash + ".bin");
                        var abs = Path.Combine(baseDir, rel);
                        if (!exists)
                        {
                            if (!File.Exists(abs)) File.WriteAllBytes(abs, chunk);
                            pInsH.Value = hash; pInsP.Value = rel.Replace('\\', '/'); pInsS.Value = chunk.Length; insertIdx.ExecuteNonQuery();
                        }
                        pH.Value = hash; pO.Value = order++; insertSnap.ExecuteNonQuery();
                        processed++; progress?.Report(processed);
                    }
                }

                tx.Commit();

                try
                {
                    string? hostBakPath = null;
                    if (serverBakDir.Contains(":"))
                    {
                        hostBakPath = serverBakPath;
                    }
                      else
                      {
                          var hostDir = Environment.GetEnvironmentVariable("MSSQL_BACKUP_HOST_DIR");
                          if (string.IsNullOrWhiteSpace(hostDir))
                          {
                              hostDir = @"C:\mssql\backup";
                          }
                          hostBakPath = Path.Combine(hostDir, bakName);
                      }
                    if (!string.IsNullOrWhiteSpace(hostBakPath) && File.Exists(hostBakPath))
                    {
                        File.Delete(hostBakPath);
                    }
                }
                catch { }

                try
                {
                    await using var del = masterConnection.CreateCommand();
                    del.CommandText = $"DECLARE @p nvarchar(4000)=@path; BEGIN TRY EXEC(''EXEC xp_cmdshell ''''del ' + REPLACE(@p, '''', '''''') + '''''''); END TRY BEGIN CATCH END CATCH; BEGIN TRY EXEC(''EXEC xp_cmdshell ''''rm -f ' + REPLACE(@p, '''', '''''') + '''''''); END TRY BEGIN CATCH END CATCH;";
                    del.Parameters.AddWithValue("@path", serverBakPath);
                    await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch { }
            }

            return backupIdOut;
        }

        public static async Task ExecuteRestoreViaBakAsync(MetadataService metadata, long backupId, Microsoft.Data.SqlClient.SqlConnection masterConnection, IProgress<int>? progress = null)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (masterConnection == null) throw new ArgumentNullException(nameof(masterConnection));
            if (backupId <= 0) throw new ArgumentOutOfRangeException(nameof(backupId));

            metadata.InitializeDatabase();
            await using var sqlite = new SqliteConnection(metadata.ConnectionString);
            await sqlite.OpenAsync().ConfigureAwait(false);

            string databaseName = "";
            await using (var nameCmd = sqlite.CreateCommand())
            {
                nameCmd.CommandText = "SELECT COALESCE(TableName,'') FROM Backups WHERE BackupID = $id";
                nameCmd.Parameters.AddWithValue("$id", backupId);
                var obj = await nameCmd.ExecuteScalarAsync().ConfigureAwait(false);
                databaseName = Convert.ToString(obj) ?? string.Empty;
            }

            if (masterConnection.State != System.Data.ConnectionState.Open)
                await masterConnection.OpenAsync().ConfigureAwait(false);

            string serverBakDir;
            await using (var p = masterConnection.CreateCommand())
            {
                p.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
                var v = await p.ExecuteScalarAsync().ConfigureAwait(false);
                var dirVal = Convert.ToString(v) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dirVal)) serverBakDir = "/var/opt/mssql/backup"; else serverBakDir = dirVal.Contains(":") ? dirVal : "/var/opt/mssql/backup";
            }
            var bakName = $"{databaseName}_{backupId}.bak";
            var sepStr = serverBakDir.Contains("/") ? "/" : "\\";
            var serverBakPath = (serverBakDir.EndsWith("/") || serverBakDir.EndsWith("\\")) ? serverBakDir + bakName : serverBakDir + sepStr + bakName;

            string hostBakPath;
            if (serverBakDir.Contains(":")) hostBakPath = serverBakPath; else {
                var hostDir = Environment.GetEnvironmentVariable("MSSQL_BACKUP_HOST_DIR");
                if (string.IsNullOrWhiteSpace(hostDir))
                {
                    hostDir = @"C:\mssql\backup";
                }
                Directory.CreateDirectory(hostDir);
                hostBakPath = Path.Combine(hostDir, bakName);
            }
            var blocksBaseDir = Path.GetDirectoryName(metadata.DatabasePath) ?? AppContext.BaseDirectory;

            if (!File.Exists(hostBakPath))
            {
                await using (var outStream = new FileStream(hostBakPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    await using var blocksCmd = sqlite.CreateCommand();
                    blocksCmd.CommandText = @"SELECT bi.storage_path FROM SnapshotBlocks sb JOIN BlockIndex bi ON bi.block_hash = sb.block_hash WHERE sb.BackupID = $id ORDER BY sb.BlockOrder";
                    blocksCmd.Parameters.AddWithValue("$id", backupId);
                    await using var rdr = await blocksCmd.ExecuteReaderAsync().ConfigureAwait(false);
                    int processed = 0;
                    while (await rdr.ReadAsync().ConfigureAwait(false))
                    {
                        var rel = rdr.GetString(0);
                        var abs = Path.Combine(blocksBaseDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        var bytes = await File.ReadAllBytesAsync(abs).ConfigureAwait(false);
                        await outStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        processed++; progress?.Report(processed);
                    }
                }
            }

            await using (var useCmd = masterConnection.CreateCommand())
            {
                useCmd.CommandText = "USE master";
                await useCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using (var single = masterConnection.CreateCommand())
            {
                single.CommandText = $"IF DB_ID(@db) IS NOT NULL ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE";
                single.Parameters.AddWithValue("@db", databaseName);
                await single.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using (var restore = masterConnection.CreateCommand())
            {
                restore.CommandText = $"RESTORE DATABASE [{databaseName}] FROM DISK = @p WITH REPLACE, RECOVERY";
                restore.Parameters.AddWithValue("@p", serverBakPath);
                await restore.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using (var multi = masterConnection.CreateCommand())
            {
                multi.CommandText = $"ALTER DATABASE [{databaseName}] SET MULTI_USER";
                await multi.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private static async IAsyncEnumerable<byte[]> ContentDefinedChunkStream(Stream input)
        {
            const int minChunk = 32 * 1024;   // 32KB
            const int avgChunk = 64 * 1024;   // 64KB (power of two)
            const int maxChunk = 256 * 1024;  // 256KB
            const int window = 64;            // rolling window size
            const ulong mask = (ulong)(avgChunk - 1); // cut when (hash & mask) == 0

            // Rabin-Karp rolling hash over a fixed window
            const ulong B = 257;
            ulong pow = 1;
            for (int i = 0; i < window; i++) pow = unchecked(pow * B);

            var readBuf = new byte[1024 * 1024];
            var win = new byte[window];
            int wCount = 0, wIdx = 0;
            ulong h = 0;

            var chunk = new MemoryStream(maxChunk);

            int read;
            while ((read = await input.ReadAsync(readBuf, 0, readBuf.Length).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    byte b = readBuf[i];
                    chunk.WriteByte(b);

                    if (wCount < window)
                    {
                        h = unchecked(h * B + b);
                        win[wCount++] = b;
                    }
                    else
                    {
                        byte old = win[wIdx];
                        h = unchecked(h * B + b - old * pow);
                        win[wIdx] = b;
                        wIdx = (wIdx + 1) % window;
                    }

                    int size = (int)chunk.Length;
                    if (size >= minChunk && (((h & mask) == 0) || size >= maxChunk))
                    {
                        yield return chunk.ToArray();
                        chunk.SetLength(0); chunk.Position = 0;
                        h = 0; wCount = 0; wIdx = 0;
                    }
                }
            }
            if (chunk.Length > 0) yield return chunk.ToArray();
        }
    }
}


