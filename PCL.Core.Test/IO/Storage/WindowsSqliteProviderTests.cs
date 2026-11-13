using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.IO.Storage;

[TestClass]
public class WindowsSqliteProviderTests
{
    [TestMethod]
    public void ConnectionsUseWindowsSystemLibrary()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        Assert.IsTrue(Version.Parse((string)command.ExecuteScalar()!) >= new Version(3, 23, 2));

        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        var sqlite = modules.Single(module =>
            string.Equals(module.ModuleName, "winsqlite3.dll", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"),
            sqlite.FileName, ignoreCase: true);
        Assert.IsFalse(modules.Any(module =>
            string.Equals(module.ModuleName, "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SupportsWalCommitRollbackReadOnlyAndPoolRelease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PCL-SystemSqlite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "数据库.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL";
                Assert.AreEqual("wal", command.ExecuteScalar());
                command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY, value TEXT)";
                command.ExecuteNonQuery();
                using (var transaction = connection.BeginTransaction())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO probe VALUES (1, '已提交')";
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
                using (var transaction = connection.BeginTransaction())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE probe SET value = '应回滚'";
                    command.ExecuteNonQuery();
                    transaction.Rollback();
                }
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM probe WHERE id = 1";
                Assert.AreEqual("已提交", command.ExecuteScalar());
            }
            // Every connection above must release its database and WAL handles.
            Directory.Delete(directory, true);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
