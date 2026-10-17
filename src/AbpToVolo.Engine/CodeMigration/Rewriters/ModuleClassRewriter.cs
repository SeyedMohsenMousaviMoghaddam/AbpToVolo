using System.Text.RegularExpressions;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Converts Boilerplate module classes:
/// PreInitialize/Initialize/PostInitialize/Shutdown → ABP.io lifecycle methods,
/// Configuration.* / IocManager.* statements → Configure&lt;TOptions&gt; / context.Services,
/// [DependsOn] module types → ABP.io module types.
/// </summary>
public sealed partial class ModuleClassRewriter : MigrationRewriter
{
    private enum Phase { None, PreConfigure, Configure, PostInit, Shutdown, Other }

    private sealed record Outcome(List<StatementSyntax> Replacements, List<string> Comments);

    private sealed record OptionsProperty(string OptionsType, string Property, string Namespace);

    /// <summary>Configuration.X.Y = value;  →  Configure&lt;TOptions&gt;(options => options.Property = value);</summary>
    private static readonly Dictionary<string, OptionsProperty> OptionAssignments = new(StringComparer.Ordinal)
    {
        ["MultiTenancy.IsEnabled"] = new("AbpMultiTenancyOptions", "IsEnabled", "Volo.Abp.MultiTenancy"),
        ["Auditing.IsEnabled"] = new("AbpAuditingOptions", "IsEnabled", "Volo.Abp.Auditing"),
        ["Auditing.IsEnabledForAnonymousUsers"] = new("AbpAuditingOptions", "IsEnabledForAnonymousUsers", "Volo.Abp.Auditing"),
        ["BackgroundJobs.IsJobExecutionEnabled"] = new("AbpBackgroundJobOptions", "IsJobExecutionEnabled", "Volo.Abp.BackgroundJobs"),
        ["Modules.AbpWebCommon().SendAllExceptionsToClients"] = new("AbpExceptionHandlingOptions", "SendExceptionsDetailsToClients", "Volo.Abp.AspNetCore.ExceptionHandling"),
    };

    private string? _module;
    private Phase _phase = Phase.None;

    public ModuleClassRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (!SyntaxHelpers.HasBaseType(node, "AbpModule"))
        {
            return base.VisitClassDeclaration(node);
        }

        var previous = _module;
        _module = node.Identifier.Text;
        Context.RequireUsing("Volo.Abp.Modularity");
        Context.RequireUsing("Volo.Abp");
        var result = base.VisitClassDeclaration(node);
        _module = previous;
        return result is ClassDeclarationSyntax module ? MoveDbContextRegistration(RemoveTemplateCode(RemoveBaseLifecycleCalls(InjectHostServices(module)))) : result;
    }

    /// <summary>
    /// Boilerplate registered the DbContext in PreInitialize; ABP.io's [ReplaceDbContext] (IIdentityDbContext,
    /// ITenantManagementDbContext...) only works when the application DbContext is registered after the module ones,
    /// i.e. in ConfigureServices (ABP.io template). The statements with AddAbpDbContext move from PreConfigureServices
    /// to the start of ConfigureServices (with their enclosing if).
    /// </summary>
    private ClassDeclarationSyntax MoveDbContextRegistration(ClassDeclarationSyntax module)
    {
        var pre = module.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "PreConfigureServices" && m.Body is not null);
        var moved = pre?.Body!.Statements.Where(s => s.ToString().Contains(".AddAbpDbContext<", StringComparison.Ordinal)).ToList() ?? [];
        if (pre is null || moved.Count == 0) return module;

        Context.Change($"Module {module.Identifier.Text}: AddAbpDbContext moved from PreConfigureServices to ConfigureServices ([ReplaceDbContext] needs it after the module DbContexts)");
        var updated = module.ReplaceNode(pre, pre.WithBody(pre.Body!.WithStatements(List(pre.Body.Statements.Where(s => !moved.Contains(s))))));

        var configure = updated.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "ConfigureServices" && m.Body is not null);
        if (configure is not null)
        {
            return updated.ReplaceNode(configure, configure.WithBody(configure.Body!.WithStatements(configure.Body.Statements.InsertRange(0, moved))));
        }

        var indent = SyntaxHelpers.Indentation(pre);
        var method = ParseMemberDeclaration($"{indent}public override void ConfigureServices(ServiceConfigurationContext context){Context.NewLine}{indent}{{{Context.NewLine}{indent}}}{Context.NewLine}")!;
        method = ((MethodDeclarationSyntax)method).WithBody(((MethodDeclarationSyntax)method).Body!.WithStatements(List(moved)));
        var preIndex = updated.Members.IndexOf(updated.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "PreConfigureServices"));
        return updated.WithMembers(updated.Members.Insert(preIndex + 1, method.WithLeadingTrivia(EndOfLine(Context.NewLine))));
    }

    /// <summary>
    /// Code of the Boilerplate web template that ABP.io does itself:
    ///   ConfigureTokenAuth() { var tokenAuthConfig = IocManager.Resolve&lt;TokenAuthConfiguration&gt;(); ... JWT ... }
    ///     → empty (ABP.io authenticates with OpenIddict: AuthServer / OpenIddict settings of the host)
    ///   ApplicationPartManager.AddApplicationPartsIfNotAddedBefore(assembly)
    ///     → removed (ABP.io adds the controllers of every module assembly)
    /// </summary>
    private ClassDeclarationSyntax RemoveTemplateCode(ClassDeclarationSyntax module)
    {
        var tokenAuth = module.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body is not null && m.Body.ToFullString().Contains("TokenAuthConfiguration", StringComparison.Ordinal))
            .ToList();
        var applicationParts = module.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "AddApplicationPartsIfNotAddedBefore" } })
            .Where(s => !tokenAuth.Any(m => m.Span.Contains(s.Span)))
            .ToList();
        if (tokenAuth.Count == 0 && applicationParts.Count == 0) return module;

        var nl = Context.NewLine;
        var updated = module.ReplaceNodes(tokenAuth.Cast<SyntaxNode>().Concat(applicationParts), (original, rewritten) =>
        {
            if (rewritten is MethodDeclarationSyntax keep && Context.ApiStyle == AbpToVolo.Core.ApiStyleMode.Boilerplate)
            {
                return KeepTokenAuthConfiguration(module, keep);
            }

            if (rewritten is MethodDeclarationSyntax method)
            {
                Context.Change($"Module {module.Identifier.Text}: {method.Identifier.Text}() — Boilerplate JWT configuration removed (ABP.io uses OpenIddict)");
                var indent = SyntaxHelpers.Indentation(method);
                var comment = Comment("// AbpToVolo: Boilerplate's TokenAuthConfiguration (JWT bearer) is replaced by OpenIddict in ABP.io: " +
                                      "configure the AuthServer / OpenIddict sections of the host's appsettings.json instead.");
                var body = Block().WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(EndOfLine(nl)))
                    .WithCloseBraceToken(Token(TriviaList(Whitespace(indent + "    "), comment, EndOfLine(nl), Whitespace(indent)), SyntaxKind.CloseBraceToken, method.Body!.CloseBraceToken.TrailingTrivia))
                    .WithLeadingTrivia(method.Body.GetLeadingTrivia());
                return method.WithBody(body);
            }

            Context.Change("ApplicationPartManager.AddApplicationPartsIfNotAddedBefore removed (ABP.io adds the controllers of every module)");
            var statement = (StatementSyntax)rewritten;
            return ParseStatement("{ }").WithLeadingTrivia(statement.GetLeadingTrivia()
                    .Add(Comment("/* AbpToVolo: AddApplicationPartsIfNotAddedBefore removed — ABP.io adds the controllers of every module assembly. */"))
                    .Add(Space))
                .WithTrailingTrivia(statement.GetTrailingTrivia());
        });
        return updated;
    }

    /// <summary>
    /// --api-style boilerplate: the TokenAuth login keeps issuing its JWT. IocManager.Register&lt;TokenAuthConfiguration&gt;() +
    /// Resolve → new TokenAuthConfiguration() { ... } registered as a singleton (ServiceConfigurationContext.Services).
    /// </summary>
    private MethodDeclarationSyntax KeepTokenAuthConfiguration(ClassDeclarationSyntax module, MethodDeclarationSyntax method)
    {
        var statements = new List<StatementSyntax>();
        string? variable = null;
        foreach (var statement in method.Body!.Statements)
        {
            var text = statement.ToString();
            if (text.Contains("TokenAuthConfiguration>", StringComparison.Ordinal) &&
                statement is ExpressionStatementSyntax && (text.Contains("Register", StringComparison.Ordinal) || text.Contains(".Add", StringComparison.Ordinal)))
            {
                continue; // registration: done below with the configured instance
            }

            if (statement is LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer: { } initializer } declarator] } local &&
                initializer.Value.ToString().Contains("TokenAuthConfiguration>", StringComparison.Ordinal))
            {
                variable = declarator.Identifier.Text;
                statements.Add(local.ReplaceNode(initializer.Value, ParseExpression("new TokenAuthConfiguration()").WithTriviaFrom(initializer.Value)));
                continue;
            }

            statements.Add(statement);
        }

        if (variable is null) return method;
        var indent = statements.Count > 0 ? SyntaxHelpers.Indentation(statements[0]) : SyntaxHelpers.Indentation(method) + "    ";
        statements.Add(ParseStatement($"ServiceConfigurationContext.Services.AddSingleton({variable});")
            .WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(EndOfLine(Context.NewLine)));
        Context.RequireUsing("Microsoft.Extensions.DependencyInjection");
        Context.Change($"Module {module.Identifier.Text}: {method.Identifier.Text}() — TokenAuthConfiguration kept (Boilerplate JWT login), registered as a singleton");
        return method.WithBody(method.Body.WithStatements(List(statements)));
    }

    /// <summary>base.PreInitialize(); etc.: the Boilerplate base methods are gone (ABP.io's are empty).</summary>
    private ClassDeclarationSyntax RemoveBaseLifecycleCalls(ClassDeclarationSyntax module)
    {
        var calls = module.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax, Name.Identifier.Text: "PreInitialize" or "Initialize" or "PostInitialize" or "Shutdown" },
                ArgumentList.Arguments.Count: 0
            })
            .ToList();
        if (calls.Count == 0) return module;

        Context.Change("Removed base.PreInitialize()/Initialize()/PostInitialize() calls (no Boilerplate base implementation in ABP.io)");
        return module.RemoveNodes(calls, SyntaxRemoveOptions.KeepNoTrivia)!;
    }

    private static readonly Dictionary<string, (string Type, string Expression)> HostServices = new(StringComparer.Ordinal)
    {
        ["IWebHostEnvironment"] = ("IWebHostEnvironment", "context.Services.GetSingletonInstance<IWebHostEnvironment>()"),
        ["IHostingEnvironment"] = ("IWebHostEnvironment", "context.Services.GetSingletonInstance<IWebHostEnvironment>()"),
        ["IHostEnvironment"] = ("IHostEnvironment", "context.Services.GetHostingEnvironment()"),
        ["IConfigurationRoot"] = ("IConfiguration", "context.Services.GetConfiguration()"),
        ["IConfiguration"] = ("IConfiguration", "context.Services.GetConfiguration()"),
    };

    /// <summary>
    /// Boilerplate modules received IWebHostEnvironment/IConfiguration in the constructor; ABP.io creates modules with a
    /// parameterless constructor. The fields are kept and set at the start of PreConfigureServices instead:
    ///   ctor(IWebHostEnvironment env) { _env = env; _appConfiguration = env.GetAppConfiguration(); }
    ///   → PreConfigureServices: _env = context.Services.GetSingletonInstance&lt;IWebHostEnvironment&gt;(); _appConfiguration = context.Services.GetConfiguration();
    /// </summary>
    private ClassDeclarationSyntax InjectHostServices(ClassDeclarationSyntax module)
    {
        var constructor = module.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault(c => c.ParameterList.Parameters.Count > 0 && c.Body is not null);
        if (constructor is null || constructor.ParameterList.Parameters.Any(p => p.Type is null || !HostServices.ContainsKey(SyntaxHelpers.SimpleName(p.Type))))
        {
            return module;
        }

        var fields = module.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Variables.Count == 1 && HostServices.ContainsKey(SyntaxHelpers.SimpleName(f.Declaration.Type)))
            .ToList();
        var assigned = constructor.Body!.Statements
            .Select(s => s is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax a } ? SyntaxHelpers.Text(a.Left).Replace("this.", "") : null)
            .ToList();
        if (assigned.Any(a => a is null || fields.All(f => f.Declaration.Variables[0].Identifier.Text != a))) return module; // other logic: keep the TODO

        var assignments = fields.Where(f => assigned.Contains(f.Declaration.Variables[0].Identifier.Text))
            .Select(f => $"{f.Declaration.Variables[0].Identifier.Text} = {HostServices[SyntaxHelpers.SimpleName(f.Declaration.Type)].Expression};")
            .ToList();

        // field types ABP.io provides: IConfigurationRoot → IConfiguration, IHostingEnvironment → IWebHostEnvironment; readonly removed
        var updated = module.ReplaceNodes(fields, (_, f) => f
            .WithModifiers(TokenList(f.Modifiers.Where(m => !m.IsKind(SyntaxKind.ReadOnlyKeyword))))
            .WithDeclaration(f.Declaration.WithType(ParseTypeName(HostServices[SyntaxHelpers.SimpleName(f.Declaration.Type)].Type).WithTriviaFrom(f.Declaration.Type))));
        updated = updated.RemoveNode(updated.Members.OfType<ConstructorDeclarationSyntax>().First(c => c.ParameterList.Parameters.Count > 0), SyntaxRemoveOptions.KeepNoTrivia)!;

        var code = updated.ToFullString();
        for (var i = assignments.Count - 1; i >= 0; i--)
        {
            code = ModuleMethodEditor.AddStatement(code, _ => true, "PreConfigureServices", "ServiceConfigurationContext context", assignments[i]) ?? code;
        }

        Context.RequireUsing("Microsoft.Extensions.DependencyInjection");
        Context.RequireUsing("Microsoft.Extensions.Configuration");
        Context.RequireUsing("Microsoft.Extensions.Hosting");
        if (fields.Any(f => HostServices[SyntaxHelpers.SimpleName(f.Declaration.Type)].Type == "IWebHostEnvironment"))
        {
            Context.RequireUsing("Microsoft.AspNetCore.Hosting");
        }

        Context.Change($"Module {module.Identifier.Text}: constructor injection → fields set in PreConfigureServices ({string.Join(" ", assignments)})");
        return ParseCompilationUnit(code).DescendantNodes().OfType<ClassDeclarationSyntax>().First().WithTriviaFrom(module);
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (_module is not null && node.ParameterList.Parameters.Count > 0 &&
            node.ParameterList.Parameters.Any(p => p.Type is null || !HostServices.ContainsKey(SyntaxHelpers.SimpleName(p.Type))))
        {
            Todo("ABP.io creates modules with a parameterless constructor (no DI). Use context.Services.GetConfiguration() / " +
                 "context.Services.GetHostingEnvironment() inside ConfigureServices instead of constructor parameters.");
        }

        return base.VisitConstructorDeclaration(node);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (_module is null || node.Parent is not ClassDeclarationSyntax)
        {
            return base.VisitMethodDeclaration(node);
        }

        var hasNoParameters = node.ParameterList.Parameters.Count == 0;
        var (phase, newName, parameter) = (node.Identifier.Text, hasNoParameters) switch
        {
            ("PreInitialize", true) => (Phase.PreConfigure, "PreConfigureServices", "ServiceConfigurationContext context"),
            ("Initialize", true) => (Phase.Configure, "ConfigureServices", "ServiceConfigurationContext context"),
            ("PostInitialize", true) => (Phase.PostInit, "OnPostApplicationInitialization", "ApplicationInitializationContext context"),
            ("Shutdown", true) => (Phase.Shutdown, "OnApplicationShutdown", "ApplicationShutdownContext context"),
            _ => (Phase.Other, (string?)null, (string?)null)
        };

        var previous = _phase;
        _phase = phase;
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        _phase = previous;

        if (newName is null)
        {
            return visited;
        }

        Context.Change($"Module {_module}: {node.Identifier.Text}() → {newName}({parameter})");
        return visited
            .WithIdentifier(Id(newName, visited.Identifier))
            .WithParameterList(ParseParameterList($"({parameter})").WithTriviaFrom(visited.ParameterList));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        if (_module is null || _phase == Phase.None)
        {
            return base.VisitBlock(node);
        }

        var statements = new List<StatementSyntax>();
        var pendingComments = new List<SyntaxTrivia>();

        void Emit(StatementSyntax statement)
        {
            if (pendingComments.Count > 0)
            {
                statement = statement.WithLeadingTrivia(pendingComments.Concat(statement.GetLeadingTrivia()));
                pendingComments.Clear();
            }

            statements.Add(statement);
        }

        foreach (var statement in node.Statements)
        {
            var outcome = TransformStatement(statement);
            if (outcome is null)
            {
                if (Visit(statement) is StatementSyntax visited) Emit(visited);
                continue;
            }

            var indent = SyntaxHelpers.Indentation(statement);
            pendingComments.AddRange(SyntaxHelpers.CommentLines(indent, Context.NewLine, outcome.Comments));
            for (var i = 0; i < outcome.Replacements.Count; i++)
            {
                var replacement = outcome.Replacements[i]
                    .WithLeadingTrivia(i == 0 ? statement.GetLeadingTrivia() : TriviaList(Whitespace(indent)))
                    .WithTrailingTrivia(i == outcome.Replacements.Count - 1 ? statement.GetTrailingTrivia() : TriviaList(EndOfLine(Context.NewLine)));
                Emit(replacement);
            }
        }

        var closeBrace = node.CloseBraceToken;
        if (pendingComments.Count > 0)
        {
            closeBrace = closeBrace.WithLeadingTrivia(pendingComments.Concat(closeBrace.LeadingTrivia));
        }

        return node.WithStatements(List(statements)).WithCloseBraceToken(closeBrace);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (_module is null || visited.Expression is not MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "IocManager" },
                Name: GenericNameSyntax { Identifier.Text: "Resolve" } resolve
            })
        {
            return visited;
        }

        if (_phase is Phase.PostInit or Phase.Shutdown)
        {
            Context.RequireUsing("Microsoft.Extensions.DependencyInjection");
            Context.Change("IocManager.Resolve<T>() → context.ServiceProvider.GetRequiredService<T>()");
            return SyntaxHelpers.Expr($"context.ServiceProvider.GetRequiredService{resolve.TypeArgumentList}()").WithTriviaFrom(visited);
        }

        Todo("Services cannot be resolved while services are being configured in ABP.io; move this code into OnApplicationInitialization(context) and use context.ServiceProvider.");
        return visited;
    }

    public override SyntaxNode? VisitAttribute(AttributeSyntax node)
    {
        if (_module is null || SyntaxHelpers.SimpleName(node.Name) is not ("DependsOn" or "DependsOnAttribute") || node.ArgumentList is null)
        {
            return base.VisitAttribute(node);
        }

        var modules = new List<string>();
        void AddModule(string name)
        {
            if (!modules.Contains(name)) modules.Add(name);
        }

        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (argument.Expression is not TypeOfExpressionSyntax typeOf)
            {
                AddModule(argument.Expression.ToString());
                continue;
            }

            var typeName = SyntaxHelpers.SimpleName(typeOf.Type);
            if (ModuleMappings.Map.TryGetValue(typeName, out var replacements))
            {
                foreach (var replacement in replacements)
                {
                    AddModule(replacement.TypeName);
                    Context.RequireUsing(replacement.Namespace);
                }

                Context.Change(replacements.Length == 0
                    ? $"[DependsOn] {typeName} removed (no ABP.io counterpart needed)"
                    : $"[DependsOn] {typeName} → {string.Join(", ", replacements.Select(r => r.TypeName))}");
            }
            else
            {
                if (typeName.StartsWith("Abp", StringComparison.Ordinal) && typeName.EndsWith("Module", StringComparison.Ordinal))
                {
                    Todo($"Unknown Boilerplate module '{typeName}' in [DependsOn]: find the ABP.io module that replaces it.");
                }

                AddModule(SyntaxHelpers.Text(typeOf.Type));
            }
        }

        foreach (var extra in LayerModules(Context.Project?.Layer ?? ProjectLayer.Unknown))
        {
            if (!modules.Contains(extra.TypeName))
            {
                AddModule(extra.TypeName);
                Context.RequireUsing(extra.Namespace);
                Context.Change($"[DependsOn] added {extra.TypeName}");
            }
        }

        if (modules.Count == 0)
        {
            modules.Add(ModuleMappings.DddDomain.TypeName);
            Context.RequireUsing(ModuleMappings.DddDomain.Namespace);
        }

        var original = node.ArgumentList;
        var multiLine = original.ToFullString().Contains('\n');
        var indent = original.Arguments.Count > 0 ? SyntaxHelpers.Indentation(original.Arguments[0]) : "    ";

        var arguments = modules.Select(m => AttributeArgument(TypeOfExpression(ParseTypeName(m)))
            .WithLeadingTrivia(multiLine ? TriviaList(Whitespace(indent)) : TriviaList()));
        var separators = Enumerable.Repeat(
            Token(SyntaxKind.CommaToken).WithTrailingTrivia(multiLine ? EndOfLine(Context.NewLine) : Space),
            Math.Max(0, modules.Count - 1));

        return node.WithArgumentList(original.WithArguments(SeparatedList(arguments, separators)));
    }

    private static IEnumerable<AbpModuleRef> LayerModules(ProjectLayer layer) => layer switch
    {
        ProjectLayer.Domain => [ModuleMappings.DddDomain],
        ProjectLayer.Application => [ModuleMappings.DddApplication, ModuleMappings.AutoMapper],
        ProjectLayer.Host or ProjectLayer.DbMigrator => [ModuleMappings.Autofac],
        _ => []
    };

    private bool HasContextParameter => _phase is Phase.PreConfigure or Phase.Configure;

    private Outcome? TransformStatement(StatementSyntax statement)
    {
        if (statement is not (ExpressionStatementSyntax or LocalDeclarationStatementSyntax))
        {
            return null;
        }

        var text = Whitespaces().Replace(SyntaxHelpers.Text(statement), " ");
        var module = _module!;

        if (text.StartsWith("IocManager.RegisterAssemblyByConvention(", StringComparison.Ordinal))
        {
            Context.Change("IocManager.RegisterAssemblyByConvention removed (conventional registration is automatic)");
            return Remove("AbpToVolo: RegisterAssemblyByConvention removed - ABP.io registers services by convention automatically.");
        }

        if (RegisterGeneric().Match(text) is { Success: true } register)
        {
            var lifetime = register.Groups["args"].Value switch
            {
                var a when a.Contains("Transient") => "AddTransient",
                var a when a.Contains("Scoped") => "AddScoped",
                _ => "AddSingleton" // Boilerplate's default lifestyle is Singleton
            };

            if (!HasContextParameter)
            {
                return CommentOut(statement, "Register this service inside ConfigureServices(context) with context.Services.");
            }

            Context.RequireUsing("Microsoft.Extensions.DependencyInjection");
            Context.Change($"IocManager.Register → context.Services.{lifetime}");
            return Replace($"context.Services.{lifetime}<{register.Groups["types"].Value}>();");
        }

        if (ProviderRegistration().Match(text) is { Success: true } provider)
        {
            var kind = provider.Groups["kind"].Value;
            return kind switch
            {
                "Authorization" or "Settings" or "Features" =>
                    Remove($"AbpToVolo: {kind} provider registration removed - ABP.io discovers definition providers automatically."),
                "Navigation" => CommentOut(statement, "Navigation providers become IMenuContributor; register via Configure<AbpNavigationOptions>(o => o.MenuContributors.Add(...))."),
                _ => CommentOut(statement, $"'{kind}' providers have no direct ABP.io counterpart.")
            };
        }

        if (text.Contains(".AbpAutoMapper().Configurators.Add(", StringComparison.Ordinal))
        {
            Context.RequireUsing("Volo.Abp.AutoMapper");
            Context.Change("AutoMapper configuration → Configure<AbpAutoMapperOptions>(options.AddMaps<Module>())");
            return Replace($"Configure<AbpAutoMapperOptions>(options => {{ options.AddMaps<{module}>(validate: false); }});");
        }

        if (OptionAssignment().Match(text) is { Success: true } assignment &&
            OptionAssignments.TryGetValue(assignment.Groups["path"].Value, out var option))
        {
            Context.RequireUsing(option.Namespace);
            Context.Change($"Configuration.{assignment.Groups["path"].Value} → Configure<{option.OptionsType}>");
            return Replace($"Configure<{option.OptionsType}>(options => {{ options.{option.Property} = {assignment.Groups["value"].Value}; }});");
        }

        if (text.StartsWith("Configuration.DefaultNameOrConnectionString", StringComparison.Ordinal))
        {
            return Remove("AbpToVolo: ABP.io reads the connection string from ConnectionStrings:Default in appsettings.json.");
        }

        if (text.Contains(".AbpEfCore().AddDbContext<", StringComparison.Ordinal) && DbContextType().Match(text) is { Success: true } dbContext)
        {
            if (!HasContextParameter)
            {
                return CommentOut(statement, "Register the DbContext inside ConfigureServices(context) with context.Services.AddAbpDbContext<T>().");
            }

            var type = dbContext.Groups["type"].Value;
            Context.RequireUsing("Volo.Abp.EntityFrameworkCore");
            Context.RequireUsing("Microsoft.Extensions.DependencyInjection");
            Context.Change($"AbpEfCore().AddDbContext<{type}> → context.Services.AddAbpDbContext<{type}> + AbpDbContextOptions");

            // The Boilerplate lambda (XDbContextConfigurer.Configure(options.DbContextOptions, options.ConnectionString):
            // provider, NetTopologySuite, retry...) is kept as it is: ABP.io's AbpDbContextConfigurationContext<T> has the
            // same DbContextOptions / ConnectionString / ExistingConnection members.
            var configureLambda = statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "AddDbContext" } })?
                .ArgumentList.Arguments.FirstOrDefault()?.Expression as LambdaExpressionSyntax;
            if (configureLambda is not null && configureLambda.ToString().Contains("Configurer.Configure(", StringComparison.Ordinal))
            {
                return new Outcome(
                    [
                        Stmt($"context.Services.AddAbpDbContext<{type}>(options => {{ options.AddDefaultRepositories(includeAllEntities: true); }});"),
                        Stmt($"Configure<AbpDbContextOptions>(options => {{ options.UseSqlServer(); options.Configure<{type}>({configureLambda}); }});")
                    ],
                    [$"AbpToVolo: {type} keeps its Boilerplate configuration (DbContextConfigurer); the other DbContexts use SQL Server."]);
            }

            return new Outcome(
                [
                    Stmt($"context.Services.AddAbpDbContext<{type}>(options => {{ options.AddDefaultRepositories(includeAllEntities: true); }});"),
                    Stmt("Configure<AbpDbContextOptions>(options => { options.UseSqlServer(); });")
                ],
                ["AbpToVolo: SQL Server assumed. Use UseNpgsql()/UseMySQL()/UseSqlite() with the matching Volo.Abp.EntityFrameworkCore.* package for other providers."]);
        }

        if (text.Contains("LocalizationConfigurer.Configure(", StringComparison.Ordinal))
        {
            if (Context.Localization is not { } loc)
            {
                return CommentOut(statement, "Configure localization with Configure<AbpLocalizationOptions>(...) and JSON resources.");
            }

            if (Context.Project is { } project && !string.Equals(project.Name, loc.ProjectName, StringComparison.OrdinalIgnoreCase))
            {
                // The JSON files live in another project (e.g. *.Domain.Shared); its module registers them.
                Context.Change($"Localization configuration moved to the {loc.ProjectName} module");
                return Remove($"AbpToVolo: localization ({loc.TypeName}) is configured in the {loc.ProjectName} module.");
            }

            Context.RequireUsing("Volo.Abp.Localization");
            Context.RequireUsing("Volo.Abp.VirtualFileSystem");
            Context.RequireUsing(loc.Namespace);
            Context.Change($"XML localization → JSON resource {loc.TypeName} ({loc.VirtualPath})");
            return new Outcome(
                [
                    Stmt($"Configure<AbpVirtualFileSystemOptions>(options => {{ options.FileSets.AddEmbedded<{module}>(); }});"),
                    Stmt($"Configure<AbpLocalizationOptions>(options => {{ {LocalizationCode.OptionsBody(loc)} }});")
                ],
                []);
        }

        if (text.Contains("CreateControllersForAppServices(", StringComparison.Ordinal))
        {
            var invocation = statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateControllersForAppServices" });
            var assembly = invocation?.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Replace(".GetAssembly()", ".Assembly");
            if (assembly is not null)
            {
                Context.RequireUsing("Volo.Abp.AspNetCore.Mvc");
                Context.Change("CreateControllersForAppServices → AbpAspNetCoreMvcOptions.ConventionalControllers.Create");
                return Replace($"Configure<AbpAspNetCoreMvcOptions>(options => {{ options.ConventionalControllers.Create({assembly}); }});");
            }
        }

        if (_phase is Phase.PostInit or Phase.Shutdown && OnlyResolveCalls().IsMatch(text))
        {
            return null; // IocManager.Resolve<T>() is converted at expression level (VisitInvocationExpression).
        }

        if (text.StartsWith("Configuration.ReplaceService", StringComparison.Ordinal))
        {
            return CommentOut(statement, "Use context.Services.Replace(ServiceDescriptor...) or [Dependency(ReplaceServices = true)] on the replacing class.");
        }

        if (text.StartsWith("Configuration.", StringComparison.Ordinal) || text.StartsWith("IocManager.", StringComparison.Ordinal) ||
            text.Contains("= Configuration.", StringComparison.Ordinal) || text.Contains(" Configuration.Modules.", StringComparison.Ordinal))
        {
            return CommentOut(statement, "Boilerplate startup configuration has no automatic ABP.io translation; reconfigure with Configure<TOptions>(...).");
        }

        return null;
    }

    private Outcome Replace(params string[] statements) => new(statements.Select(Stmt).ToList(), []);

    private static Outcome Remove(string comment) => new([], [comment]);

    private Outcome CommentOut(StatementSyntax statement, string reason)
    {
        Context.Change("Commented out unsupported module configuration");
        return new Outcome([], SyntaxHelpers.CommentOutLines(statement, reason).ToList());
    }

    private static StatementSyntax Stmt(string code) => SyntaxHelpers.Stmt(code);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespaces();

    [GeneratedRegex(@"^IocManager\.Register<(?<types>.+)>\((?<args>.*)\);$")]
    private static partial Regex RegisterGeneric();

    [GeneratedRegex(@"^Configuration\.(?<kind>\w+)\.Providers\.Add<.+>\(\);$")]
    private static partial Regex ProviderRegistration();

    [GeneratedRegex(@"^Configuration\.(?<path>[\w\.\(\)]+?) = (?<value>.+);$")]
    private static partial Regex OptionAssignment();

    /// <summary>Statements whose only IocManager usages are Resolve&lt;T&gt;() calls.</summary>
    [GeneratedRegex(@"^(?!.*IocManager\.(?!Resolve<)).*IocManager\.Resolve<")]
    private static partial Regex OnlyResolveCalls();

    [GeneratedRegex(@"AddDbContext<(?<type>[\w\.]+)>")]
    private static partial Regex DbContextType();
}
