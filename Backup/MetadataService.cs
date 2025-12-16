using Microsoft.Data.Sqlite;

namespace Backup
{
    public class MetadataService
    {
        private readonly string _databasePath;
        private readonly string _connectionString;

        public MetadataService(string? databasePath = null)
        {
            _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "metadata.db");

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default
            };

            _connectionString = builder.ToString();
        }

        public string ConnectionString => _connectionString;
        public string DatabasePath => _databasePath;

        public void InitializeDatabase()
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();

            var createBackups = @"
CREATE TABLE IF NOT EXISTS Backups (
    BackupID INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp DATETIME NOT NULL,
    Type TEXT NOT NULL,
    TableName TEXT
);";

            var createBlockIndex = @"
CREATE TABLE IF NOT EXISTS BlockIndex (
    block_hash TEXT PRIMARY KEY,
    storage_path TEXT NOT NULL,
    block_size INTEGER NOT NULL
);";

            var createSnapshotBlocks = @"
CREATE TABLE IF NOT EXISTS SnapshotBlocks (
    BackupID INTEGER NOT NULL,
    block_hash TEXT NOT NULL,
    BlockOrder INTEGER NOT NULL,
    FOREIGN KEY (BackupID) REFERENCES Backups(BackupID) ON DELETE CASCADE,
    FOREIGN KEY (block_hash) REFERENCES BlockIndex(block_hash) ON DELETE RESTRICT
);";

            foreach (var sql in new[] { createBackups, createBlockIndex, createSnapshotBlocks })
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var dropLegacy = connection.CreateCommand())
            {
                dropLegacy.Transaction = transaction;
                dropLegacy.CommandText = "DROP TABLE IF EXISTS SnapshotTables";
                dropLegacy.ExecuteNonQuery();
            }

            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "PRAGMA table_info('Backups')";
                using var reader = checkCmd.ExecuteReader();
                var hasTableName = false;
                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, "TableName", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTableName = true;
                        break;
                    }
                }
                if (!hasTableName)
                {
                    using var alterCmd = connection.CreateCommand();
                    alterCmd.Transaction = transaction;
                    alterCmd.CommandText = "ALTER TABLE Backups ADD COLUMN TableName TEXT";
                    alterCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }
}
