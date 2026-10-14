namespace AbpToVolo.Tests;

public class ModelRewriterTests : RewriterTestBase
{
    [Fact]
    public void Int_key_shortcuts_and_audit_members_are_converted()
    {
        const string source = """
            using Abp.Domain.Entities.Auditing;
            using Abp.Application.Services.Dto;

            public class Person : FullAuditedEntity
            {
                public string Name { get; set; }
            }

            public class PersonDto : EntityDto
            {
                public long? CreatorUserId { get; set; }
                public void Copy(Person p, EntityDto other) { CreatorUserId = p.CreatorUserId; }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public class Person : FullAuditedEntity<int>", code);
        AssertContains("public class PersonDto : EntityDto<int>", code);
        AssertContains("public void Copy(Person p, EntityDto<int> other) { CreatorId = p.CreatorId; }", code);
        AssertContains("public Guid? CreatorId { get; set; }", code); // ABP.io audit user ids are Guid
        AssertContains("using Volo.Abp.Domain.Entities.Auditing;", code);
        AssertContains("using Volo.Abp.Auditing;", code);
        AssertContains("using Volo.Abp.Application.Dtos;", code);
    }

    [Fact]
    public void Attributes_are_converted()
    {
        const string source = """
            using Abp.Authorization;
            using Abp.AutoMapper;
            using Abp.Web.Models;

            [AbpAuthorize("Pages.Users", "Pages.Admin")]
            public class UsersController
            {
                [DontWrapResult]
                [AbpAllowAnonymous]
                public void Ping() { }
            }

            [AutoMapFrom(typeof(User), typeof(Admin))]
            public class UserDto { }

            [AutoMapTo(typeof(User))]
            public class CreateUserDto { }
            """;

        var (code, _) = Migrate(source);

        AssertContains("[Authorize(\"Pages.Users\")]", code);
        AssertContains("[Authorize] accepts a single policy", code);
        AssertContains("[AllowAnonymous]", code);
        AssertContains("[DontWrapResult]", code); // Boilerplate API style (default): compatibility attribute
        AssertNotContains("DontWrapResult", Migrate(source, apiStyle: AbpToVolo.Core.ApiStyleMode.Abp).Code);
        AssertContains("[AutoMap(typeof(User)), AutoMap(typeof(Admin))]", code);
        AssertContains("[AutoMap(typeof(User), ReverseMap = true)]", code);
        AssertContains("using AutoMapper;", code);
        AssertContains("using Microsoft.AspNetCore.Authorization;", code);
    }

    [Fact]
    public void Localization_calls_are_converted()
    {
        const string source = """
            using Abp.Localization;

            public class MyAppService : ApplicationService
            {
                public MyAppService() { LocalizationSourceName = AcmeConsts.LocalizationSourceName; }
                public string Hello(string name) => L("Hello", name);
            }

            public class MyAuthorizationProvider : AuthorizationProvider
            {
                private static ILocalizableString L(string name) => new LocalizableString(name, AcmeConsts.LocalizationSourceName);
                public string X() => L("Keep").ToString();
            }
            """;

        var (code, _) = Migrate(source, localization: PhoneBookResource);

        AssertContains("LocalizationResource = typeof(PhoneBookResource);", code);
        AssertContains("=> L[\"Hello\", name];", code);
        AssertContains("=> LocalizableString.Create<PhoneBookResource>(name);", code);
        AssertContains("L(\"Keep\")", code); // class declares its own L method
    }

    [Fact]
    public void Zero_db_context_becomes_abp_db_context_with_module_tables()
    {
        const string source = """
            using Abp.Zero.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore;

            namespace Acme.EntityFrameworkCore
            {
                public class AcmeDbContext : AbpZeroDbContext<Tenant, Role, User, AcmeDbContext>
                {
                    public AcmeDbContext(DbContextOptions<AcmeDbContext> options) : base(options) { }
                }
            }
            """;

        var (code, context) = Migrate(source);

        AssertContains("[ConnectionStringName(\"Default\")]", code);
        AssertContains("public class AcmeDbContext : AbpDbContext<AcmeDbContext>", code);
        AssertContains("builder.ConfigureIdentity();", code);
        AssertContains("builder.ConfigureTenantManagement();", code);
        AssertContains("using Volo.Abp.Identity.EntityFrameworkCore;", code);
        Assert.True(context.UsesZero);
    }

    [Fact]
    public void Existing_OnModelCreating_receives_module_configuration_after_base_call()
    {
        const string source = """
            public class AcmeDbContext : AbpZeroDbContext<Tenant, Role, User, AcmeDbContext>
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.Entity<Person>().ToTable("People");
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("base.OnModelCreating(modelBuilder); modelBuilder.ConfigurePermissionManagement();", code);
    }

    [Fact]
    public void Permission_provider_becomes_permission_definition_provider()
    {
        const string source = """
            using Abp.Authorization;
            using Abp.MultiTenancy;

            public class AcmeAuthorizationProvider : AuthorizationProvider
            {
                public override void SetPermissions(IPermissionDefinitionContext context)
                {
                    var users = context.CreatePermission(PermissionNames.Pages_Users, L("Users"));
                    users.CreateChildPermission(PermissionNames.Pages_Users_Create, L("Create"));
                    context.CreatePermission(PermissionNames.Pages_Tenants, L("Tenants"), multiTenancySides: MultiTenancySides.Host);
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public class AcmeAuthorizationProvider : PermissionDefinitionProvider", code);
        AssertContains("public override void Define(IPermissionDefinitionContext context)", code);
        AssertContains("var group = context.AddGroup(\"Acme\");", code);
        AssertContains("var users = group.AddPermission(PermissionNames.Pages_Users, L(\"Users\"));", code);
        AssertContains("users.AddChild(PermissionNames.Pages_Users_Create, L(\"Create\"));", code);
        AssertContains("group.AddPermission(PermissionNames.Pages_Tenants, L(\"Tenants\"), multiTenancySide: MultiTenancySides.Host);", code);
        AssertNotContains("TODO", code); // fully converted, nothing to report
    }

    [Fact]
    public void Setting_provider_becomes_setting_definition_provider()
    {
        const string source = """
            using System.Collections.Generic;
            using Abp.Configuration;

            public class AppSettingProvider : SettingProvider
            {
                public override IEnumerable<SettingDefinition> GetSettingDefinitions(SettingDefinitionProviderContext context)
                {
                    return new[]
                    {
                        new SettingDefinition("Theme", "red", scopes: SettingScopes.User, clientVisibilityProvider: new VisibleSettingClientVisibilityProvider()),
                        new SettingDefinition("Secret", "x", isEncrypted: true)
                    };
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public class AppSettingProvider : SettingDefinitionProvider", code);
        AssertContains("public override void Define(ISettingDefinitionContext context)", code);
        AssertContains("new SettingDefinition(\"Theme\", \"red\", isVisibleToClients: true)", code);
        AssertContains("new SettingDefinition(\"Secret\", \"x\", isEncrypted: true)", code);
    }

    [Fact]
    public void Usings_are_mapped_and_unknown_namespaces_reported()
    {
        const string source = """
            // header comment
            using System;
            using Abp.Domain.Entities;
            using Abp.Notifications;
            using Castle.Windsor;

            public class Foo { }
            """;

        var (code, context) = Migrate(source);

        AssertContains("using Volo.Abp.Domain.Entities; using Volo.Abp;", code);
        AssertContains("// header comment", code);
        AssertNotContains("using Abp.", code);
        AssertNotContains("Castle", code);
        Assert.Contains(context.FileTodos, t => t.Contains("Abp.Notifications"));
        AssertContains("// ---- AbpToVolo: manual work required ----", code);
    }

    [Fact]
    public void Module_zero_classes_are_flagged()
    {
        const string source = """
            using Abp.Authorization.Users;
            public class User : AbpUser<User> { }
            public class UserManager : AbpUserManager<Role, User> { }
            """;

        var (code, context) = Migrate(source);

        Assert.True(context.UsesZero);
        AssertContains("Replace it with Volo.Abp.Identity.IdentityUser", code);
        AssertContains("Replace it with Volo.Abp.Identity.IdentityUserManager", code);
    }

    [Fact]
    public void Untouched_code_is_left_byte_for_byte_identical()
    {
        const string source = "using System;\n\nnamespace Acme\n{\n    public class Plain { public int X => 1; }\n}\n";
        var (code, context) = Migrate(source);

        Assert.Equal(source, code);
        Assert.Empty(context.Changes);
    }
}
