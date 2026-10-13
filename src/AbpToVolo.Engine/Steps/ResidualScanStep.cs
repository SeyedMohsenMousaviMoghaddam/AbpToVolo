using System.Text.RegularExpressions;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Scans the converted code for Boilerplate constructs that the rewriters could not translate and lists every
/// TODO [AbpToVolo] comment with its line number, so the report is a complete work list.
/// </summary>
public sealed partial class ResidualScanStep : IMigrationStep
{
    private static readonly (Regex Pattern, string Message)[] Rules =
    [
        (new Regex(@"\bIocManager\b"), "Castle Windsor IocManager: use constructor injection, context.Services or IServiceProvider."),
        (new Regex(@"\bAbpSession\b"), "AbpSession: use CurrentUser / CurrentTenant."),
        (new Regex(@"\bAbpBootstrapper\b"), "AbpBootstrapper: use AbpApplicationFactory.CreateAsync<TModule>() (see ABP.io DbMigrator template)."),
        (new Regex(@"\b(IShouldNormalize|ICustomValidate)\b"), "IShouldNormalize/ICustomValidate: use IValidatableObject or FluentValidation."),
        (new Regex(@"\bIPassivable\b"), "IPassivable has no ABP.io equivalent; declare an IsActive property."),
        (new Regex(@"\bAbpServiceBase\b"), "AbpServiceBase: derive from ApplicationService or DomainService."),
        (new Regex(@"\bAbp(User|Role|Tenant)(Base)?<"), "Module Zero entity: replace with the ABP.io Identity/TenantManagement module entities."),
        (new Regex(@"\b(AbpUserManager|AbpRoleManager|AbpLogInManager|AbpLoginManager)\b"), "Module Zero manager: use IdentityUserManager / IdentityRoleManager / SignInManager."),
        (new Regex(@"\bNullableIdDto\b"), "NullableIdDto: declare a DTO with 'TKey? Id'."),
        (new Regex(@"\b(long|int)\??\s+(CreatorId|LastModifierId|DeleterId)\b"), "Audit user ids are Guid? in ABP.io."),
        (new Regex(@"\bAbpDataFilters\b"), "AbpDataFilters: use IDataFilter.Disable<ISoftDelete>() / Disable<IMultiTenant>()."),
        (new Regex(@"\bIRealTimeNotifier|INotificationPublisher|IUserNotificationManager\b"), "Boilerplate notification system has no ABP.io counterpart (use distributed events + SignalR)."),
    ];

    public string Name => "Residual scan";

    public void Execute(MigrationContext context)
    {
        var findings = 0;
        foreach (var file in FileSystem.EnumerateFiles(context.OutputRoot, "*.cs"))
        {
            if (context.IsExcluded(file))
            {
                continue;
            }

            var relative = context.Relative(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var todoIndex = line.IndexOf(SyntaxHelpers.TodoPrefix, StringComparison.Ordinal);
                if (todoIndex >= 0)
                {
                    // File-level TODOs are reported by the C# step already.
                    if (!IsFileHeader(lines, i))
                    {
                        context.Report.Todo(Name, line[(todoIndex + SyntaxHelpers.TodoPrefix.Length)..].Trim(), relative, i + 1);
                        findings++;
                    }

                    continue;
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var (pattern, message) in Rules)
                {
                    if (pattern.IsMatch(line))
                    {
                        context.Report.Todo(Name, $"{message}  →  `{trimmed.Trim()}`", relative, i + 1);
                        findings++;
                    }
                }
            }
        }

        context.Log.WriteLine($"    {findings} items need manual attention");
    }

    private static bool IsFileHeader(string[] lines, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (lines[i].Contains("AbpToVolo: manual work required", StringComparison.Ordinal)) return true;
            if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) return false;
        }

        return false;
    }
}
