using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Backup
{
    public partial class MainForm : Form
    {
        private readonly MetadataService _metadata;
        private SqlConnection? _masterConn;
        private string _databaseName = string.Empty;

        public MainForm()
        {
            InitializeComponent();
            _metadata = new MetadataService(Path.Combine(Environment.CurrentDirectory, "metadata.db"));
            _metadata.InitializeDatabase();
            LoadBackupsToGrid();
            dgvBackups.SelectionChanged += dgvBackups_SelectionChanged;
            UpdateRestoreButtonEnabled();
        }

        private string BuildSqlConnectionString()
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = txtServer.Text?.Trim() ?? string.Empty,
                InitialCatalog = txtDatabase.Text?.Trim() ?? string.Empty,
                IntegratedSecurity = false,
                Encrypt = true,
                TrustServerCertificate = true
            };

            var user = txtUser.Text?.Trim();
            var pass = txtPassword.Text ?? string.Empty;
            if (!string.IsNullOrEmpty(user))
            {
                builder.UserID = user;
                builder.Password = pass;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            return builder.ToString();
        }

        private async void btnTestConnection_Click(object sender, EventArgs e)
        {
            var connStr = BuildSqlConnectionString();
            try
            {
                var csb = new SqlConnectionStringBuilder(connStr) { InitialCatalog = "master" };
                var dbName = txtDatabase.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dbName))
                    throw new ArgumentException("База даних обов'язкова");

                if (_masterConn != null)
                {
                    try { _masterConn.Dispose(); } catch { }
                    _masterConn = null;
                }

                var conn = new SqlConnection(csb.ToString());
                await conn.OpenAsync();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM sys.databases WHERE name = @n";
                    cmd.Parameters.AddWithValue("@n", dbName);
                    var ok = await cmd.ExecuteScalarAsync();
                    if (ok == null)
                        throw new InvalidOperationException($"Базу даних '{dbName}' не знайдено на сервері.");
                }

                _masterConn = conn;
                _databaseName = dbName;
                AppendLog("Підключення успішне. Сеанс збережено для резервування/відновлення.");
            }
            catch (Exception ex)
            {
                AppendLog("Помилка підключення: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Помилка підключення", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void btnCreateBackup_Click(object sender, EventArgs e)
        {
            try
            {
                SetBusy(true);
                var backupType = "Full";
                AppendLog("Починаю створення повного резервного копіювання бази...");
                if (_masterConn == null || string.IsNullOrWhiteSpace(_databaseName))
                    throw new InvalidOperationException("Спочатку виконайте 'Тест підключення'.");

                var idAll = await BackupService.ExecuteBackupViaBakAsync(_metadata, _masterConn, _databaseName, backupType);
                AppendLog($"Резервну копію створено. ID={idAll}");
                LoadBackupsToGrid();
            }
            catch (Exception ex)
            {
                AppendLog("Помилка резервного копіювання: " + ex.Message);
                MessageBox.Show(this, ex.ToString(), "Помилка резервного копіювання", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void btnRestore_Click(object sender, EventArgs e)
        {
            if (dgvBackups.CurrentRow == null)
            {
                MessageBox.Show(this, "Оберіть копію для відновлення.", "Попередження", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var backupId = Convert.ToInt64(dgvBackups.CurrentRow.Cells[0].Value);

            try
            {
                SetBusy(true);
                AppendLog($"Починаю відновлення з ID={backupId} (.bak)...");
                if (_masterConn == null)
                    throw new InvalidOperationException("Спочатку виконайте 'Тест підключення'.");

                await BackupService.ExecuteRestoreViaBakAsync(_metadata, backupId, _masterConn);
                AppendLog("Відновлення завершено.");
            }
            catch (Exception ex)
            {
                AppendLog("Помилка відновлення: " + ex.Message);
                MessageBox.Show(this, ex.ToString(), "Помилка відновлення", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void LoadBackupsToGrid()
        {
            using var conn = new SqliteConnection(_metadata.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT BackupID, Timestamp, Type, COALESCE(TableName,'') FROM Backups ORDER BY BackupID DESC";
            using var rdr = cmd.ExecuteReader();

            dgvBackups.Rows.Clear();
            while (rdr.Read())
            {
                var id = rdr.GetInt64(0);
                var ts = rdr.GetDateTime(1);
                var type = rdr.GetString(2);
                var table = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3);
                dgvBackups.Rows.Add(id, ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), type, table);
            }
            UpdateRestoreButtonEnabled();
        }

        private void AppendLog(string message)
        {
            if (txtLog.TextLength > 0) txtLog.AppendText(Environment.NewLine);
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void dgvBackups_SelectionChanged(object? sender, EventArgs e)
        {
            UpdateRestoreButtonEnabled();
        }

        private void UpdateRestoreButtonEnabled()
        {
            btnRestore.Enabled = dgvBackups.CurrentRow != null && dgvBackups.SelectedRows.Count > 0;
            btnDelete.Enabled = btnRestore.Enabled;
        }

        private void SetBusy(bool isBusy)
        {
            UseWaitCursor = isBusy;
            btnCreateBackup.Enabled = !isBusy;
            btnRestore.Enabled = !isBusy && (dgvBackups.CurrentRow != null && dgvBackups.SelectedRows.Count > 0);
            btnDelete.Enabled = !isBusy && (dgvBackups.CurrentRow != null && dgvBackups.SelectedRows.Count > 0);
            btnTestConnection.Enabled = !isBusy;
        }

        private async void btnDelete_Click(object sender, EventArgs e)
        {
            if (dgvBackups.CurrentRow == null) return;
            var backupId = Convert.ToInt64(dgvBackups.CurrentRow.Cells[0].Value);
            var confirm = MessageBox.Show(this, $"Видалити копію #{backupId}?", "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                SetBusy(true);
                AppendLog($"Видаляю копію ID={backupId}...");
                await BackupService.DeleteBackupAsync(_metadata, backupId);
                AppendLog("Копію видалено.");
                LoadBackupsToGrid();
            }
            catch (Exception ex)
            {
                AppendLog("Помилка видалення: " + ex.Message);
                MessageBox.Show(this, ex.ToString(), "Помилка видалення", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }
    }
}

