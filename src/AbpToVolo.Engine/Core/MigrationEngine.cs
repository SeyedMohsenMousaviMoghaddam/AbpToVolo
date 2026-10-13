using System.Diagnostics;
using AbpToVolo.Steps;

namespace AbpToVolo.Core;

public interface IMigrationStep
{
    string Name { get; }
    bool IsEnabled(MigrationOptions options) => true;
    void Execute(MigrationContext context);
}

public sealed class MigrationEngine
{
    private readonly IReadOnlyList<IMigrationStep> _steps;

    public MigrationEngine(IEnumerable<IMigrationStep>? steps = null)
    {
        _steps = (steps ?? DefaultSteps()).ToList();
    }

    public IReadOnlyList<IMigrationStep> Steps => _steps;

    /// <summary>The order matters: later steps rely on the project layout and data produced by earlier ones.</summary>
    public static IEnumerable<IMigrationStep> DefaultSteps() =>
    [
        new CopySourceStep(),
        new DetectSolutionStep(),
        new RenameLayersStep(),        // ABP.io project names (Core → Domain ...)
        new StandardNamesStep(),       // ABP.io module class names (XCoreModule → XDomainModule ...)
        new ContractsScaffoldStep(),   // before localization: the JSON resources go to *.Domain.Shared
        new LocalizationStep(),
        new EfMigrationsStep(),        // before the C# step: migration folders are excluded from rewriting
        new CSharpCodeStep(),
        new ModuleZeroStep(),          // User/Role/Tenant → Identity/TenantManagement, Zero infrastructure → legacy
        new ServiceBasesStep(),        // {Prefix}DomainService / {Prefix}AppService bases (ABP.io template convention)
        new ContractsStep(),           // needs the converted code (PermissionDefinitionProvider, [AutoMap]...)
        new AbpModulesStep(),          // Identity/Account/OpenIddict/... module dependencies, DbContext integration
        new LayerServicesStep(),       // framework services each layer injects (HttpClient, SignalR)
        new HostStandardizationStep(), // ABP.io HttpApi.Host (module + Program.cs) and DbMigrator
        new TestProjectsStep(),        // needs the final layout (interfaces in Application.Contracts)
        new CompatShimStep(),
        new ResolveVersionsStep(),
        new ProjectFileStep(),
        new BuildFixStep(),            // builds with the real compiler and fixes what the diagnostics point to
        new ResidualScanStep(),
        new BuildVerificationStep(),
        new ReportWriterStep()
    ];

    /// <summary>Simple API: plain-text progress to a TextWriter (console by default).</summary>
    public MigrationReport Run(MigrationOptions options, TextWriter? log = null) =>
        Run(options, new TextWriterObserver(log ?? Console.Out));

    /// <summary>
    /// Runs every enabled step. The observer receives step start/end (with duration and counts), progress, log lines
    /// and report entries. Cancelling stops between steps or inside long steps (OperationCanceledException).
    /// </summary>
    public MigrationReport Run(MigrationOptions options, IMigrationObserver observer, CancellationToken cancellationToken = default)
    {
        var report = new MigrationReport();
        var context = new MigrationContext(options, report, observer, cancellationToken);
        report.EntryAdded += observer.Entry;

        var total = Stopwatch.StartNew();
        observer.RunStarted(options, _steps.Select(s => s.Name).ToList());

        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var info = new StepInfo(i, _steps.Count, step.Name);
            if (!step.IsEnabled(options))
            {
                observer.StepSkipped(info, "disabled by the options");
                continue;
            }

            var changes = report.Count(ReportLevel.Change);
            var todos = report.Count(ReportLevel.Todo);
            var warnings = report.Count(ReportLevel.Warning);
            var watch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.CurrentStep = info;
                observer.StepStarted(info);
                step.Execute(context);
                context.Log.Flush();
            }
            catch (Exception ex)
            {
                observer.StepFailed(info, ex);
                observer.RunCompleted(report, total.Elapsed, success: false);
                report.EntryAdded -= observer.Entry;
                throw;
            }

            observer.StepCompleted(info, new StepSummary(watch.Elapsed,
                report.Count(ReportLevel.Change) - changes,
                report.Count(ReportLevel.Todo) - todos,
                report.Count(ReportLevel.Warning) - warnings));
        }

        observer.RunCompleted(report, total.Elapsed, success: true);
        report.EntryAdded -= observer.Entry;
        return report;
    }
}
