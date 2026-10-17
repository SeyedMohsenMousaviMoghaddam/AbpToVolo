using AbpToVolo.Core;
using AbpToVolo.Steps;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Tests;

public class ContractsAndIdTests : RewriterTestBase
{
    [Fact]
    public void Zero_constants_become_their_boilerplate_values()
    {
        const string source = """
            using System.ComponentModel.DataAnnotations;
            public class UserDto
            {
                [StringLength(AbpUserBase.MaxUserNameLength)] public string UserName { get; set; }
                [StringLength(AbpTenantBase.MaxTenancyNameLength)] public string TenancyName { get; set; }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("[StringLength(256)] public string UserName", code);
        AssertContains("[StringLength(64)] public string TenancyName", code);
    }

    [Fact]
    public void NameValueDto_becomes_NameValue()
    {
        var (code, _) = Migrate("using Abp.Application.Services.Dto; public class X { public NameValueDto<int> A { get; set; } public NameValueDto B { get; set; } }");

        AssertContains("public NameValue<int> A", code);
        AssertContains("public NameValue B", code);
        AssertContains("using Volo.Abp;", code);
    }

    [Fact]
    public void Tenant_and_audit_user_ids_become_guid()
    {
        const string source = """
            using Abp.Domain.Entities;
            public class Invoice : Entity<long>, IMayHaveTenant
            {
                public int? TenantId { get; set; }
            }
            public class InvoiceDto
            {
                public long? CreatorUserId { get; set; }
                public int? TenantId { get; set; }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public class Invoice : Entity<long>, IMultiTenant", code);
        AssertContains("public Guid? TenantId { get; set; }", code);
        AssertContains("public Guid? CreatorId { get; set; }", code);
        // --id-type source (default): the DTO keeps the int tenant id of the source (the API does not change);
        // --id-type guid: it follows the entity.
        AssertContains("public int? TenantId { get; set; }", code);
        Assert.DoesNotContain("int? TenantId", Migrate(source, idType: IdTypeMode.Guid).Code);
    }

    [Theory]
    [InlineData("public enum Status { A, B }", true)]
    [InlineData("public static class AppConsts { public const string Name = \"x\"; public const int Max = 5; }", true)]
    [InlineData("public class PermissionNames { public const string Pages = \"Pages\"; public static readonly string Other = \"o\"; }", true)]
    [InlineData("public class Person : Entity<int> { public const int Max = 5; }", false)]
    [InlineData("public class Helper { public static int Add(int a) => a; }", false)]
    public void Shared_kind_classification(string code, bool expected)
    {
        var decl = CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>().First();
        Assert.Equal(expected, ContractsStep.IsSharedKind(decl));
    }

    [Theory]
    [InlineData("public class PersonDto : EntityDto<int> { }", "src/App/People/PersonDto.cs", true)]
    [InlineData("public class GetPeopleInput : PagedAndSortedResultRequestDto { }", "src/App/People/X.cs", true)]
    [InlineData("public class Filter { public string Text { get; set; } }", "src/App/People/Dto/Filter.cs", true)]
    [InlineData("public interface IPersonAppService : IApplicationService { }", "src/App/People/IPersonAppService.cs", true)]
    [InlineData("public class PersonAppService : ApplicationService, IPersonAppService { }", "src/App/People/PersonAppService.cs", false)]
    [InlineData("public class PeopleMapProfile : Profile { }", "src/App/People/Dto/PeopleMapProfile.cs", false)]
    [InlineData("public class Helper { }", "src/App/People/Helper.cs", false)]
    public void Contract_kind_classification(string code, string path, bool expected)
    {
        var decl = CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>().First();
        Assert.Equal(expected, ContractsStep.IsContractKind(decl, path));
    }

    [Fact]
    public void Designer_rename_keeps_the_migration_id()
    {
        const string designer = """
            using Abp.Authorization;
            using Microsoft.EntityFrameworkCore.Migrations;
            namespace Acme.Migrations
            {
                [DbContext(typeof(AcmeDbContext))]
                [Migration("20250101000000_Last")]
                partial class Last
                {
                    protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
                }
            }
            """;

        var renamed = EfMigrationsStep.RenameDesigner(designer, "Last");

        Assert.Contains("[Migration(\"20250101000000_Last\")]", renamed);
        Assert.Contains($"partial class {EfMigrationsStep.SquashedClassName}", renamed);
        Assert.DoesNotContain("using Abp.", renamed);
    }
}
