using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// An index's INCLUDE columns and NULLS [NOT] DISTINCT are compared, and the settings EF Core can't set, e.g. a
/// row-level security policy, are reported as extra in the database
/// </summary>
public class PostgresIndexAndTableSettingsTests
{
    private readonly ITestOutputHelper _output;

    public PostgresIndexAndTableSettingsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void IndexesEfCoreCreatedMatch()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(null);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void DifferentIncludeColumnsAndNullsDistinctAreReported()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(
            """
            DROP INDEX ix_account_email;
            CREATE INDEX ix_account_email ON account (email);
            DROP INDEX ix_account_name;
            CREATE INDEX ix_account_name ON account (name) INCLUDE (email);
            DROP INDEX ix_account_code;
            CREATE UNIQUE INDEX ix_account_code ON account (code);
            DROP INDEX ix_account_owner_id;
            CREATE UNIQUE INDEX ix_account_owner_id ON account (owner_id) NULLS NOT DISTINCT;
            """);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: Account->Index 'code', nulls distinct. Expected = NULLS NOT DISTINCT, found = NULLS DISTINCT",
            "DIFFERENT: Account->Index 'email', include columns. Expected = name, found = <null>",
            "DIFFERENT: Account->Index 'name', include columns. Expected = <null>, found = email",
            "DIFFERENT: Account->Index 'owner_id', nulls distinct. Expected = NULLS DISTINCT, found = NULLS NOT DISTINCT",
        });
    }

    [Fact]
    public void SettingsEfCoreCantSetAreReportedAsExtra()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(
            """
            CREATE INDEX ix_lower_email ON account (lower(email));
            ALTER TABLE account ENABLE ROW LEVEL SECURITY;
            ALTER TABLE account FORCE ROW LEVEL SECURITY;
            CREATE POLICY p_all ON account USING (true);
            CREATE RULE r_no_delete AS ON DELETE TO account DO INSTEAD NOTHING;
            ALTER TABLE account DROP CONSTRAINT fk_account_owner_owner_id;
            ALTER TABLE account ADD CONSTRAINT fk_account_owner_owner_id FOREIGN KEY (owner_id) REFERENCES owner (id)
                ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED NOT VALID;
            ALTER TABLE account DROP CONSTRAINT pk_account;
            ALTER TABLE account ADD CONSTRAINT pk_account PRIMARY KEY (id) INCLUDE (email);
            ALTER TABLE account DROP CONSTRAINT ak_account_name;
            ALTER TABLE account ADD CONSTRAINT ak_account_name UNIQUE NULLS NOT DISTINCT (name) INCLUDE (email);
            """);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Index 'account', index constraint name. Found = ix_lower_email",
            "EXTRA IN DATABASE: Table 'account', deferrable. Found = fk_account_owner_owner_id DEFERRABLE INITIALLY DEFERRED",
            "EXTRA IN DATABASE: Table 'account', include columns. Found = ak_account_name INCLUDE (email)",
            "EXTRA IN DATABASE: Table 'account', include columns. Found = pk_account INCLUDE (email)",
            "EXTRA IN DATABASE: Table 'account', not valid. Found = fk_account_owner_owner_id NOT VALID",
            "EXTRA IN DATABASE: Table 'account', nulls distinct. Found = ak_account_name NULLS NOT DISTINCT",
            "EXTRA IN DATABASE: Table 'account', policy. Found = p_all",
            "EXTRA IN DATABASE: Table 'account', row level security. Found = ENABLE ROW LEVEL SECURITY",
            "EXTRA IN DATABASE: Table 'account', row level security. Found = FORCE ROW LEVEL SECURITY",
            "EXTRA IN DATABASE: Table 'account', rule. Found = r_no_delete",
        });
    }

    [Fact]
    public void ExtraSettingCanBeIgnored()
    {
        //SETUP
        var config = new CompareEfSqlConfig();
        config.IgnoreTheseErrors("EXTRA IN DATABASE: Table 'account', policy. Found = p_all");

        //ATTEMPT
        var errors = CompareAfterRunning("ALTER TABLE account ENABLE ROW LEVEL SECURITY; CREATE POLICY p_all ON account USING (true);", config);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Table 'account', row level security. Found = ENABLE ROW LEVEL SECURITY",
        });
    }

    //------------------------------------------------------

    private string[] CompareAfterRunning(string sql, CompareEfSqlConfig config = null)
    {
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new AccountContext(new DbContextOptionsBuilder<AccountContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        if (sql != null)
            context.Database.ExecuteSqlRaw(sql);

        var comparer = new CompareEfSql(config);
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);
        _output.WriteLine(comparer.GetAllErrors);
        //Sorted, as the order the errors are found in isn't what is being tested
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private class Owner
    {
        public int Id { get; set; }
    }

    private class Account
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public int? Code { get; set; }
        public int OwnerId { get; set; }
        public Owner Owner { get; set; }
    }

    private class AccountContext : DbContext
    {
        public AccountContext(DbContextOptions<AccountContext> options) : base(options) { }

        public DbSet<Account> Accounts { get; set; }
        public DbSet<Owner> Owners { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(entity =>
            {
                entity.ToTable("owner");
                entity.HasKey(e => e.Id).HasName("pk_owner");
                entity.Property(e => e.Id).HasColumnName("id");
            });
            modelBuilder.Entity<Account>(entity =>
            {
                entity.ToTable("account");
                entity.HasKey(e => e.Id).HasName("pk_account");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
                entity.Property(e => e.Email).HasColumnName("email");
                entity.Property(e => e.Code).HasColumnName("code");
                entity.Property(e => e.OwnerId).HasColumnName("owner_id");
                entity.HasAlternateKey(e => e.Name).HasName("ak_account_name");
                //The test project also references SQL Server, which has an IncludeProperties too
                NpgsqlIndexBuilderExtensions.IncludeProperties(
                    entity.HasIndex(e => e.Email).HasDatabaseName("ix_account_email"), e => e.Name);
                entity.HasIndex(e => e.Name).HasDatabaseName("ix_account_name");
                entity.HasIndex(e => e.Code).HasDatabaseName("ix_account_code").IsUnique().AreNullsDistinct(false);
                entity.HasIndex(e => e.OwnerId).HasDatabaseName("ix_account_owner_id").IsUnique();
                entity.HasOne(e => e.Owner).WithMany().HasForeignKey(e => e.OwnerId).HasConstraintName("fk_account_owner_owner_id");
            });
        }
    }
}
