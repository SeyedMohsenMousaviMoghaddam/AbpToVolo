using AbpToVolo.Core;
using AbpToVolo.Steps;
using Microsoft.CodeAnalysis.CSharp;

namespace AbpToVolo.Tests;

/// <summary>Module Zero ids in the solution's own tables and code: user/tenant/role foreign keys and audit columns → Guid.</summary>
public sealed class ZeroKeysTests : RewriterTestBase
{
    private const string Snapshot = """
        partial class AppDbContextModelSnapshot : ModelSnapshot
        {
            protected override void BuildModel(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity("App.Authorization.Users.User", b =>
                    {
                        b.Property<long>("Id")
                            .HasColumnType("bigint");

                        b.ToTable("AbpUsers");
                    });

                modelBuilder.Entity("App.Notes.Note", b =>
                    {
                        b.Property<int>("Id")
                            .HasColumnType("int");

                        b.Property<long?>("CreatorUserId")
                            .HasColumnType("bigint");

                        b.Property<long>("OwnerUserId")
                            .HasColumnType("bigint");

                        b.Property<int?>("TenantId")
                            .HasColumnType("int");

                        b.Property<int>("Count")
                            .HasColumnType("int");

                        b.HasKey("Id");

                        b.HasIndex("CreatorUserId");

                        b.ToTable("Notes");
                    });

                modelBuilder.Entity("App.Notes.Note", b =>
                    {
                        b.HasOne("App.Authorization.Users.User", "CreatorUser")
                            .WithMany()
                            .HasForeignKey("CreatorUserId");

                        b.Navigation("CreatorUser");
                    });
            }
        }
        """;

    private static bool IsZero(string entity) => entity == "App.Authorization.Users.User";

    [Fact]
    public void Foreign_keys_and_audit_columns_of_own_tables_are_found()
    {
        var root = CSharpSyntaxTree.ParseText(Snapshot).GetCompilationUnitRoot();
        var conversions = ZeroForeignKeys.FindConversions(root, IsZero);

        Assert.Equal(3, conversions.Count); // not the Zero table itself, not Count/Id
        var creator = Assert.Single(conversions, c => c.Column == "CreatorUserId");
        Assert.Equal(("Notes", "CreatorId", "User", true), (creator.Table, creator.NewColumn, creator.Kind, creator.Nullable));
        Assert.Contains(conversions, c => c is { Column: "OwnerUserId", NewColumn: "OwnerUserId", Kind: "User", Nullable: false });
        Assert.Contains(conversions, c => c is { Column: "TenantId", Kind: "Tenant" });
    }

    [Fact]
    public void Snapshot_follows_the_converted_model()
    {
        var root = CSharpSyntaxTree.ParseText(Snapshot).GetCompilationUnitRoot();
        var updated = ZeroForeignKeys.UpdateSnapshot(root, ZeroForeignKeys.FindConversions(root, IsZero), IsZero);

        Assert.Contains("b.Property<Guid?>(\"CreatorId\")", updated);
        Assert.Contains("b.Property<Guid>(\"OwnerUserId\")", updated);
        Assert.Contains("b.Property<Guid?>(\"TenantId\")", updated);
        Assert.Contains("b.HasIndex(\"CreatorId\")", updated);
        Assert.Contains("b.Property<int>(\"Count\")", updated);
        Assert.Contains("b.Property<long>(\"Id\")", updated); // the Zero entity is untouched
        Assert.DoesNotContain("HasForeignKey(\"CreatorUserId\")", updated);
        Assert.DoesNotContain("b.Navigation(\"CreatorUser\")", updated);
        Assert.Empty(CSharpSyntaxTree.ParseText(updated).GetDiagnostics());
    }

    [Fact]
    public void Migration_converts_each_column_through_the_id_map()
    {
        var root = CSharpSyntaxTree.ParseText(Snapshot).GetCompilationUnitRoot();
        var migration = ZeroForeignKeys.Migration("App.Migrations", ZeroForeignKeys.FindConversions(root, IsZero), t => "Zero_" + t);

        Assert.Contains("EXEC dbo.Zero_ConvertColumnToGuid N'dbo', N'Notes', N'CreatorUserId', N'CreatorId', N'User', N'null';", migration);
        Assert.Contains("N'OwnerUserId', N'OwnerUserId', N'User', N'fail';", migration); // a real foreign key: dangling ids stop the migration
        Assert.Contains("FROM dbo.[Zero_AbpUsers] z", migration);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.Zero_ConvertColumnToGuid", migration);
        Assert.Empty(CSharpSyntaxTree.ParseText(migration).GetDiagnostics());
    }

    [Fact]
    public void AbpIoModules_migration_keeps_the_zero_tables_and_copies_the_data_before_the_foreign_keys()
    {
        const string migration = """
            using Microsoft.EntityFrameworkCore.Migrations;

            public partial class AbpIoModules : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropTable(
                        name: "Zero_AbpUsers");

                    migrationBuilder.CreateTable(
                        name: "AbpUsers",
                        columns: table => new { });

                    migrationBuilder.AddForeignKey(
                        name: "FK_Notes_AbpUsers_CreatorId",
                        table: "Notes",
                        column: "CreatorId",
                        principalTable: "AbpUsers",
                        principalColumn: "Id");
                }

                protected override void Down(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropTable(
                        name: "AbpUsers");

                    migrationBuilder.CreateTable(
                        name: "Zero_AbpUsers",
                        columns: table => new { });
                }
            }
            """;

        var (code, dropped) = AbpIoModulesMigrationStep.MakeSafe(migration, "INSERT INTO dbo.[AbpUsers] SELECT \"x\";");

        Assert.Equal(1, dropped);
        Assert.DoesNotContain("name: \"Zero_AbpUsers\"", code);
        var sql = code.IndexOf("migrationBuilder.Sql(", StringComparison.Ordinal);
        Assert.True(code.IndexOf("CreateTable(", StringComparison.Ordinal) < sql && sql < code.IndexOf("AddForeignKey(", StringComparison.Ordinal));
        AssertContains("SELECT \"\"x\"\";", code); // verbatim string escaping
        Assert.Empty(CSharpSyntaxTree.ParseText(code).GetDiagnostics());
    }

    [Fact]
    public void Custom_repositories_get_the_transaction_from_their_DbContext()
    {
        const string source = """
            using Abp.Data;
            using Abp.EntityFrameworkCore;
            public abstract class AppRepositoryBase<TEntity> : EfCoreRepositoryBase<AppDbContext, TEntity, int>
            {
                internal IActiveTransactionProvider _transactionProvider { get; set; }

                protected AppRepositoryBase(IDbContextProvider<AppDbContext> dbContextProvider, IActiveTransactionProvider transactionProvider)
                    : base(dbContextProvider)
                {
                    _transactionProvider = transactionProvider;
                }

                internal async Task<DbTransaction> GetActiveTransaction()
                {
                    return (DbTransaction)(await _transactionProvider.GetActiveTransactionAsync(new ActiveTransactionProviderArgs { { "ContextType", typeof(AppDbContext) } }));
                }
            }

            public class PersonRepository : AppRepositoryBase<Person>
            {
                public PersonRepository(IDbContextProvider<AppDbContext> dbContextProvider, IActiveTransactionProvider transactionProvider)
                    : base(dbContextProvider, transactionProvider)
                {
                }
            }
            """;

        var (code, _) = Migrate(source);

        Assert.DoesNotContain("IActiveTransactionProvider", code);
        AssertContains("(await GetDbContextAsync()).Database.CurrentTransaction?.GetDbTransaction()", code);
        AssertContains(": base(dbContextProvider)", code);
        AssertContains("using Microsoft.EntityFrameworkCore.Storage;", code);
    }

    [Fact]
    public void Role_ids_and_audit_property_names_in_strings_follow_abp_io()
    {
        const string source = """
            using Abp.Domain.Entities.Auditing;
            using System.ComponentModel.DataAnnotations.Schema;
            public class Note : FullAuditedEntity
            {
                public int RoleId { get; set; }
                [ForeignKey("CreatorUserId")]
                public User CreatorUser { get; set; }
            }
            """;

        var (code, _) = Migrate(source, idType: IdTypeMode.Guid);

        Assert.Contains("public Guid RoleId { get; set; }", code);
        Assert.Contains("[ForeignKey(\"CreatorId\")]", code);

        // --id-type source (default): the application's references keep their type; audit fields are ABP.io's (Guid)
        var (legacy, _) = Migrate(source);
        Assert.Contains("public int RoleId { get; set; }", legacy);
        Assert.Contains("[ForeignKey(\"CreatorId\")]", legacy);
    }

    [Fact]
    public void Source_id_type_converts_only_tenant_and_audit_columns()
    {
        var root = CSharpSyntaxTree.ParseText(Snapshot).GetCompilationUnitRoot();
        var conversions = ZeroForeignKeys.FindConversions(root, IsZero, IdTypeMode.Source);

        Assert.Contains(conversions, c => c.Column == "CreatorUserId");
        Assert.Contains(conversions, c => c.Column == "TenantId");
        Assert.DoesNotContain(conversions, c => c.Column == "OwnerUserId"); // the application's own reference keeps long
    }

    [Fact]
    public void Tenants_keep_their_source_id_display_name_and_tenancy_name()
    {
        var sql = ZeroDatabase.DataMigrationSql(new Dictionary<string, string>(),
            [(Mappings.ZeroKind.Tenant, "TenancyName"), (Mappings.ZeroKind.Tenant, "IsActive")], legacyIds: true);

        // The old int id → LegacyId; Name = the Boilerplate display name; TenancyName and IsActive → extension columns.
        Assert.Contains("ConcurrencyStamp, [TenancyName], [IsActive], [LegacyId])", sql);
        Assert.Contains("SELECT m.NewId, n.Name, UPPER(n.Name), 0,", sql);
        Assert.Contains(", t.[TenancyName], t.[IsActive], t.Id", sql);
        Assert.Contains("LEFT(COALESCE(NULLIF(LTRIM(RTRIM(t.Name)), N''), t.TenancyName), 64)", sql);
        Assert.Contains("ALTER SEQUENCE dbo.AbpTenantLegacyIds RESTART WITH", sql);

        // Without --id-type source no LegacyId column.
        Assert.DoesNotContain("[LegacyId]", ZeroDatabase.DataMigrationSql(new Dictionary<string, string>(), [], legacyIds: false));
    }

    [Fact]
    public void Renamed_members_keep_their_name_in_anonymous_types()
    {
        var declarator = (Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousObjectMemberDeclaratorSyntax)
            ((Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousObjectCreationExpressionSyntax)SyntaxFactory.ParseExpression("new\n{\n    x.Id,\n    x.Name\n}")).Initializers[0];

        var named = CodeMigration.SyntaxHelpers.NamedMember(declarator, "Id", SyntaxFactory.ParseExpression("EF.Property<int>(x, \"LegacyId\")"));

        Assert.Equal("    Id = EF.Property<int>(x, \"LegacyId\")", named.ToFullString()); // the line break belongs to "{"
    }
}
