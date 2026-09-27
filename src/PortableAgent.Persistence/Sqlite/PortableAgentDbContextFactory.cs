using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PortableAgent.Persistence.Sqlite;

public sealed class PortableAgentDbContextFactory : IDesignTimeDbContextFactory<PortableAgentDbContext>
{
    public PortableAgentDbContext CreateDbContext(string[] args) => Open(Path.GetFullPath("portable-agent.db"), create: true);

    internal static PortableAgentDbContext Open(string path, bool create = false)
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 5
        };
        return new(new DbContextOptionsBuilder<PortableAgentDbContext>().UseSqlite(connection.ToString()).Options);
    }
}
