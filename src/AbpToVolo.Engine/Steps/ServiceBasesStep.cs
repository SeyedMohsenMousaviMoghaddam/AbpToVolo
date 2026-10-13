using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// ABP.io template convention: every project has its own base classes for services ({Prefix}AppService : ApplicationService).
/// Boilerplate's AbpServiceBase / BackgroundJob gave services and jobs L(), LocalizationManager, SettingManager,
/// CurrentUnitOfWork...; ABP.io's DomainService and background jobs only have the basics. This step generates
///   {Prefix}DomainService : DomainService                            (Domain)
///   {Prefix}AsyncBackgroundJob&lt;TArgs&gt;, {Prefix}BackgroundJob&lt;TArgs&gt;  (Domain)
///   {Prefix}AppService : ApplicationService                          (Application)
/// with those members (resolved lazily through ABP's IAbpLazyServiceProvider), and makes the classes that derive
/// directly from DomainService / ApplicationService / AsyncBackgroundJob / BackgroundJob derive from them.
/// </summary>
public sealed class ServiceBasesStep : IMigrationStep
{
    public string Name => "ABP.io service base classes";

    public void Execute(MigrationContext context)
    {
        var prefix = new string(context.ModulePrefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        var localization = context.PrimaryLocalization;
        var settingManagement = context.UsesIdentityModules;
        var domain = context.FindProject(ProjectLayer.Domain);
        var application = context.FindProject(ProjectLayer.Application);
        var projects = new[] { domain, application }.OfType<ProjectInfo>().ToList();

        if (domain is not null)
        {
            var name = $"{prefix}DomainService";
            var count = Retarget(context, [domain], "DomainService", name, domain.RootNamespace);
            if (count > 0)
            {
                FileSystem.WriteText(Path.Combine(domain.Directory, name + ".cs"), DomainServiceBase(domain.RootNamespace, name, localization, settingManagement));
                context.Report.Change(Name, $"Generated {name} (L, LocalizationManager, SettingManager, CurrentUnitOfWork, DataFilter...) and made {count} domain services derive from it",
                    context.Relative(domain.CsprojPath));
            }

            foreach (var baseName in new[] { "AsyncBackgroundJob", "BackgroundJob" })
            {
                var jobBase = $"{prefix}{baseName}";
                var jobs = Retarget(context, projects, baseName, jobBase, domain.RootNamespace);
                if (jobs == 0) continue;
                FileSystem.WriteText(Path.Combine(domain.Directory, "BackgroundJobs", jobBase + ".cs"), JobBase(domain.RootNamespace, jobBase, baseName, localization, settingManagement));
                context.Report.Change(Name, $"Generated {jobBase}<TArgs> and made {jobs} background jobs derive from it", context.Relative(domain.CsprojPath));
            }
        }

        if (application is not null)
        {
            var name = $"{prefix}AppService";
            var count = Retarget(context, [application], "ApplicationService", name, application.RootNamespace);
            if (count > 0)
            {
                FileSystem.WriteText(Path.Combine(application.Directory, name + ".cs"), AppServiceBase(application.RootNamespace, name, localization, settingManagement));
                context.Report.Change(Name, $"Generated {name} and made {count} application services derive from it", context.Relative(application.CsprojPath));
            }
        }
    }

    /// <summary>class X : DomainService → class X : {Prefix}DomainService (also generic: AsyncBackgroundJob&lt;T&gt;); only direct derivations.</summary>
    private static int Retarget(MigrationContext context, IEnumerable<ProjectInfo> projects, string baseName, string newBase, string newBaseNamespace)
    {
        var count = 0;
        foreach (var file in projects.SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs")).Where(f => !context.IsExcluded(f)).Distinct())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(baseName, StringComparison.Ordinal)) continue;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var targets = root.DescendantNodes().OfType<SimpleBaseTypeSyntax>()
                .Where(b => b.Parent?.Parent is ClassDeclarationSyntax cls && cls.Identifier.Text != newBase && b == ((BaseListSyntax)b.Parent).Types[0] &&
                            b.Type switch
                            {
                                IdentifierNameSyntax id => id.Identifier.Text == baseName,
                                GenericNameSyntax g => g.Identifier.Text == baseName,
                                _ => false
                            })
                .ToList();
            if (targets.Count == 0) continue;

            var updated = root.ReplaceNodes(targets, (original, _) => original.WithType(original.Type switch
            {
                GenericNameSyntax g => g.WithIdentifier(Identifier(newBase).WithTriviaFrom(g.Identifier)),
                var t => IdentifierName(newBase).WithTriviaFrom(t)
            }));
            var code = ModuleMethodEditor.AddUsings(updated.ToFullString(), [newBaseNamespace]);
            FileSystem.WriteText(file, code, FileSystem.ReadText(file).Encoding);
            count += targets.Count;
        }

        return count;
    }

    /// <summary>The members Boilerplate's service/job base classes had, resolved through LazyServiceProvider.</summary>
    private static string CompatMembers(LocalizationResourceInfo? localization, bool settingManagement, bool declareLazyServiceProvider) => $$"""
        {{(declareLazyServiceProvider ? """
            /* Set by property injection (as in ABP.io's ApplicationService). */
            public IAbpLazyServiceProvider LazyServiceProvider { get; set; }

            protected ICurrentTenant CurrentTenant => LazyServiceProvider.LazyGetRequiredService<ICurrentTenant>();

            protected IClock Clock => LazyServiceProvider.LazyGetRequiredService<IClock>();

            protected IGuidGenerator GuidGenerator => LazyServiceProvider.LazyGetService<IGuidGenerator>(SimpleGuidGenerator.Instance);


        """ : "")}}    private IStringLocalizer _localizer;

            protected Type LocalizationResource { get; set; }{{(localization is null ? "" : $" = typeof({localization.TypeName});")}}

            protected IStringLocalizerFactory StringLocalizerFactory => LazyServiceProvider.LazyGetRequiredService<IStringLocalizerFactory>();

            /* Boilerplate's ILocalizationManager: GetString(sourceName, key) is provided by LocalizationManagerCompatExtensions. */
            protected IStringLocalizerFactory LocalizationManager => StringLocalizerFactory;

            protected IStringLocalizer L => _localizer ??= LocalizationResource is null
                ? StringLocalizerFactory.CreateDefaultOrNull()
                : StringLocalizerFactory.Create(LocalizationResource);

            protected ISettingProvider SettingProvider => LazyServiceProvider.LazyGetRequiredService<ISettingProvider>();
        {{(settingManagement ? """

            protected ISettingManager SettingManager => LazyServiceProvider.LazyGetRequiredService<ISettingManager>();
        """ : "")}}

            protected IUnitOfWorkManager UnitOfWorkManager => LazyServiceProvider.LazyGetRequiredService<IUnitOfWorkManager>();

            protected IUnitOfWork CurrentUnitOfWork => UnitOfWorkManager.Current;

            protected IDataFilter DataFilter => LazyServiceProvider.LazyGetRequiredService<IDataFilter>();

            protected ICurrentUser CurrentUser => LazyServiceProvider.LazyGetRequiredService<ICurrentUser>();

            protected IObjectMapper ObjectMapper => LazyServiceProvider.LazyGetRequiredService<IObjectMapper>();

            protected IAuthorizationService AuthorizationService => LazyServiceProvider.LazyGetRequiredService<IAuthorizationService>();

            protected IPermissionChecker PermissionChecker => LazyServiceProvider.LazyGetRequiredService<IPermissionChecker>();
        """;

    private static string Usings(LocalizationResourceInfo? localization, bool settingManagement, params string[] extra) =>
        string.Concat(new[]
            {
                "System", "Microsoft.AspNetCore.Authorization", "Microsoft.Extensions.Localization", "Volo.Abp.Authorization.Permissions", "Volo.Abp.Data",
                "Volo.Abp.DependencyInjection", "Volo.Abp.Localization", "Volo.Abp.ObjectMapping", "Volo.Abp.Settings", "Volo.Abp.Uow", "Volo.Abp.Users"
            }
            .Concat(settingManagement ? ["Volo.Abp.SettingManagement"] : [])
            .Concat(localization is null ? [] : [localization.Namespace])
            .Concat(extra)
            .Distinct()
            .OrderBy(u => u.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(u => u, StringComparer.Ordinal)
            .Select(u => $"using {u};{Environment.NewLine}"));

    private static string DomainServiceBase(string ns, string name, LocalizationResourceInfo? localization, bool settingManagement) => $$"""
        {{Usings(localization, settingManagement, "Volo.Abp.Domain.Services")}}
        namespace {{ns}};

        /// <summary>
        /// Base class of the domain services of this solution (generated by AbpToVolo, ABP.io template convention).
        /// Provides the members Boilerplate's AbpServiceBase had (L, LocalizationManager, SettingManager, CurrentUnitOfWork...), resolved lazily.
        /// </summary>
        public abstract class {{name}} : DomainService
        {
        {{CompatMembers(localization, settingManagement, declareLazyServiceProvider: false)}}
        }

        """;

    private static string JobBase(string ns, string name, string baseName, LocalizationResourceInfo? localization, bool settingManagement) => $$"""
        {{Usings(localization, settingManagement, "Volo.Abp.BackgroundJobs", "Volo.Abp.Guids", "Volo.Abp.MultiTenancy", "Volo.Abp.Timing")}}
        namespace {{ns}};

        /// <summary>
        /// Base class of the background jobs of this solution (generated by AbpToVolo). Provides the members Boilerplate's
        /// BackgroundJob had (L, LocalizationManager, SettingManager, CurrentUnitOfWork, UnitOfWorkManager...).
        /// </summary>
        public abstract class {{name}}<TArgs> : {{baseName}}<TArgs>
        {
        {{CompatMembers(localization, settingManagement, declareLazyServiceProvider: true)}}
        }

        """;

    private static string AppServiceBase(string ns, string name, LocalizationResourceInfo? localization, bool settingManagement) => $$"""
        using Microsoft.Extensions.Localization;
        using Volo.Abp.Application.Services;
        {{(settingManagement ? "using Volo.Abp.SettingManagement;" + Environment.NewLine : "")}}{{(localization is null ? "" : $"using {localization.Namespace};{Environment.NewLine}")}}
        namespace {{ns}};

        /// <summary>
        /// Base class of the application services of this solution (generated by AbpToVolo, like the ABP.io template's
        /// {Project}AppService). Sets the localization resource{{(settingManagement ? " and exposes SettingManager" : "")}} and LocalizationManager.
        /// </summary>
        public abstract class {{name}} : ApplicationService
        {
            protected {{name}}()
            {
        {{(localization is null ? "" : $"        LocalizationResource = typeof({localization.TypeName});{Environment.NewLine}")}}    }

            /* Boilerplate's ILocalizationManager: GetString(sourceName, key) is provided by LocalizationManagerCompatExtensions. */
            protected IStringLocalizerFactory LocalizationManager => StringLocalizerFactory;
        {{(settingManagement ? """

            protected ISettingManager SettingManager => LazyServiceProvider.LazyGetRequiredService<ISettingManager>();
        """ : "")}}
        }

        """;
}
