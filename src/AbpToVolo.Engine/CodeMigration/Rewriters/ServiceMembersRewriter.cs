using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Members inherited from Boilerplate base classes (AbpServiceBase, AbpController...) that have different
/// names in ABP.io: AbpSession, PermissionChecker, SettingManager, CurrentUnitOfWork.DisableFilter,
/// EventBus.TriggerAsync, static Clock, event handler signatures.
/// </summary>
public sealed class ServiceMembersRewriter : MigrationRewriter
{
    private static readonly Dictionary<string, string> DataFilters = new(StringComparer.Ordinal)
    {
        ["SoftDelete"] = "ISoftDelete",
        ["MayHaveTenant"] = "IMultiTenant",
        ["MustHaveTenant"] = "IMultiTenant",
    };

    private sealed record SettingMethod(string Name, bool DropMiddleArgument = false, bool Sync = false);

    /// <summary>Boilerplate ISettingManager → Volo.Abp.SettingManagement ISettingManager extension methods.</summary>
    private static readonly Dictionary<string, SettingMethod> SettingManagerMethods = new(StringComparer.Ordinal)
    {
        ["GetSettingValueAsync"] = new("GetOrNullForCurrentUserAsync"),
        ["GetSettingValue"] = new("GetOrNullForCurrentUserAsync", Sync: true),
        ["GetSettingValueForApplicationAsync"] = new("GetOrNullGlobalAsync"),
        ["GetSettingValueForApplication"] = new("GetOrNullGlobalAsync", Sync: true),
        ["GetSettingValueForTenantAsync"] = new("GetOrNullForTenantAsync"),
        ["GetSettingValueForTenant"] = new("GetOrNullForTenantAsync", Sync: true),
        ["GetSettingValueForUserAsync"] = new("GetOrNullForUserAsync", DropMiddleArgument: true),
        ["ChangeSettingForApplicationAsync"] = new("SetGlobalAsync"),
        ["ChangeSettingForApplication"] = new("SetGlobalAsync", Sync: true),
        ["ChangeSettingForTenantAsync"] = new("SetForTenantAsync"),
        ["ChangeSettingForUserAsync"] = new("SetForUserAsync"),
    };

    private static readonly Dictionary<string, string> CastleLogMethods = new(StringComparer.Ordinal)
    {
        ["Debug"] = "LogDebug",
        ["Info"] = "LogInformation",
        ["Warn"] = "LogWarning",
        ["Error"] = "LogError",
        ["Fatal"] = "LogCritical",
        ["DebugFormat"] = "LogDebug",
        ["InfoFormat"] = "LogInformation",
        ["WarnFormat"] = "LogWarning",
        ["ErrorFormat"] = "LogError",
    };

    private static readonly string[] ServiceBaseSuffixes =
        ["AppService", "ApplicationService", "AppServiceBase", "DomainService", "Controller", "ControllerBase", "Manager", "BackgroundJob", "Hub"];

    public ServiceMembersRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        var receiver = visited.Expression;
        var member = visited.Name.Identifier.Text;

        // AbpUserBase.MaxUserNameLength → 256 (Module Zero constants keep their original values)
        if (receiver is IdentifierNameSyntax zeroType &&
            TypeMappings.ZeroConstants.TryGetValue(zeroType.Identifier.Text, out var constants) &&
            constants.TryGetValue(member, out var literal))
        {
            Context.Change($"{zeroType.Identifier.Text}.{member} → {literal}");
            return SyntaxHelpers.Expr(literal).WithTriviaFrom(visited);
        }

        // AbpSession.UserId → CurrentUser.Id ; AbpSession.TenantId → CurrentTenant.Id
        if (IsIdentifier(receiver, "AbpSession"))
        {
            Context.RequireUsing("Volo.Abp.Users");
            Context.RequireUsing("Volo.Abp.MultiTenancy");
            // IdTypeMode.Source: the application's user ids stay long — the legacy id of the current user (claim).
            var legacy = Context.IdType == IdTypeMode.Source;
            var replacement = member switch
            {
                "UserId" => legacy ? "CurrentUser.GetLegacyId()" : "CurrentUser.Id",
                "GetUserId" => legacy ? "CurrentUser.GetRequiredLegacyId" : "CurrentUser.GetId",
                "TenantId" => "CurrentTenant.Id",
                "GetTenantId" => "CurrentTenant.GetId",
                "ImpersonatorUserId" => "CurrentUser.FindImpersonatorUserId",
                // UserIdentifier(tenantId, userId) → the user id: ABP.io identifies a user by its id alone.
                "ToUserIdentifier" => "CurrentUser.GetId",
                _ => null
            };

            if (replacement is null)
            {
                Todo($"AbpSession.{member} has no direct ABP.io equivalent (see ICurrentUser / ICurrentTenant).");
                return visited;
            }

            // The Guid id change is reported once in the report's "Next steps", not on every line.
            Context.Change($"AbpSession.{member} → {replacement}");
            return SyntaxHelpers.Expr(replacement).WithTriviaFrom(visited);
        }

        // _abpSession.UserId (injected IAbpSession, now ICurrentUser)
        if (receiver is IdentifierNameSyntax session && session.Identifier.Text.Contains("abpSession", StringComparison.OrdinalIgnoreCase))
        {
            if (Context.IdType == IdTypeMode.Source && member is "UserId" or "GetUserId")
            {
                Context.Change($"{session.Identifier.Text}.{member} → legacy user id (IdTypeMode.Source)");
                return member == "UserId"
                    ? SyntaxHelpers.Expr($"{session.Identifier.Text}.GetLegacyId()").WithTriviaFrom(visited)
                    : visited.WithName(IdentifierName("GetRequiredLegacyId").WithTriviaFrom(visited.Name));
            }

            var renamed = member switch { "UserId" => "Id", "GetUserId" => "GetId", "ToUserIdentifier" => "GetId", _ => null };
            if (renamed is not null)
            {
                Context.Change($"{session.Identifier.Text}.{member} → {renamed}");
                return visited.WithName(IdentifierName(renamed).WithTriviaFrom(visited.Name));
            }
        }

        // Static Abp.Timing.Clock outside services → DateTime.Now
        if (IsIdentifier(receiver, "Clock") && member == "Now" && !IsInsideServiceClass(node))
        {
            Context.RequireUsing("System");
            Context.Change("Static Clock.Now → DateTime.Now (inject IClock for testable time)");
            return SyntaxHelpers.Expr("DateTime.Now").WithTriviaFrom(visited);
        }

        return visited;
    }

    /// <summary>await settingManager.GetSettingValueForTenantAsync&lt;short&gt;(n, t) → (await settingManager.GetOrNullForTenantAsync(n, t)).To&lt;short&gt;()</summary>
    public override SyntaxNode? VisitAwaitExpression(AwaitExpressionSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } access } invocation &&
            SyntaxHelpers.Text(access.Expression).EndsWith("settingManager", StringComparison.OrdinalIgnoreCase) &&
            SettingManagerMethods.TryGetValue(generic.Identifier.Text, out var method) && !method.Sync && generic.TypeArgumentList.Arguments.Count == 1)
        {
            Context.RequireUsing("Volo.Abp.SettingManagement");
            Context.RequireUsing("System");
            Context.Change($"SettingManager.{generic.Identifier.Text}<T> → {method.Name} + To<T>()");
            var args = invocation.ArgumentList.Arguments.Select(a => ((ArgumentSyntax)Visit(a)!).ToString()).ToList();
            if (method.DropMiddleArgument && args.Count == 3) args.RemoveAt(1);
            var receiver = ((ExpressionSyntax)Visit(access.Expression)!).WithoutTrivia();
            return SyntaxHelpers.Expr($"(await {receiver}.{method.Name}({string.Join(", ", args)})).To<{generic.TypeArgumentList.Arguments[0]}>()").WithTriviaFrom(node);
        }

        return base.VisitAwaitExpression(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (visited.Expression is not MemberAccessExpressionSyntax access)
        {
            return visited;
        }

        var receiver = SyntaxHelpers.Text(access.Expression);
        var method = access.Name.Identifier.Text;

        // AbpSession.Use(tenantId, userId) → CurrentTenant.Change(tenantId) (ABP.io switches the tenant; the user comes from the principal)
        if (method == "Use" && (receiver == "AbpSession" || receiver.Contains("abpSession", StringComparison.OrdinalIgnoreCase)) && visited.ArgumentList.Arguments.Count >= 1)
        {
            Context.RequireUsing("Volo.Abp.MultiTenancy");
            Context.Change("AbpSession.Use(tenantId, userId) → CurrentTenant.Change(tenantId)");
            if (visited.ArgumentList.Arguments.Count > 1)
            {
                Todo("AbpSession.Use also switched the user; in ABP.io use ICurrentPrincipalAccessor.Change(claims) if the user matters here.");
            }

            return SyntaxHelpers.Expr($"CurrentTenant.Change({visited.ArgumentList.Arguments[0]})").WithTriviaFrom(visited);
        }

        // BackgroundJobManager.EnqueueAsync<TJob, TArgs>(args) → EnqueueAsync<TArgs>(args) (ABP.io finds the job by its args type)
        if (access.Name is GenericNameSyntax { Identifier.Text: "EnqueueAsync" or "Enqueue", TypeArgumentList.Arguments.Count: 2 } enqueue)
        {
            Context.Change("EnqueueAsync<TJob, TArgs> → EnqueueAsync<TArgs>");
            var name = GenericName(Identifier("EnqueueAsync"), TypeArgumentList(SingletonSeparatedList(enqueue.TypeArgumentList.Arguments[1])));
            if (enqueue.Identifier.Text == "Enqueue") Todo("Enqueue (sync) → await EnqueueAsync.");
            return visited.WithExpression(access.WithName(name));
        }

        // Injected ILocalizationManager (now IStringLocalizerFactory): _localizationManager.GetString(source, key) → _localizationManager.Create(typeof(Resource))[key]
        if (access.Name.Identifier.Text == "GetString" && SyntaxHelpers.Text(access.Expression).EndsWith("localizationManager", StringComparison.Ordinal) &&
            SyntaxHelpers.Text(access.Expression) != "LocalizationManager" && visited.ArgumentList.Arguments.Count >= 2 && Context.Localization is { } resource)
        {
            Context.RequireUsing(resource.Namespace);
            Context.Change("ILocalizationManager.GetString(source, key) → IStringLocalizerFactory.Create(typeof(Resource))[key]");
            return SyntaxHelpers.Expr($"{SyntaxHelpers.Text(access.Expression)}.Create(typeof({resource.TypeName}))[{visited.ArgumentList.Arguments[1]}]").WithTriviaFrom(visited);
        }

        // Castle ILogger (Logger.Info) → Microsoft ILogger (Logger.LogInformation)
        if (receiver is "Logger" or "_logger" or "logger" && CastleLogMethods.TryGetValue(method, out var logMethod))
        {
            Context.RequireUsing("Microsoft.Extensions.Logging");
            Context.Change($"Logger.{method} → Logger.{logMethod}");
            var arguments = visited.ArgumentList;

            // Castle: Logger.Error(exception) → Microsoft: LogError(exception, exception.Message)
            if (arguments.Arguments.Count == 1 && arguments.Arguments[0].Expression is IdentifierNameSyntax { Identifier.Text: var single } &&
                (single is "e" or "ex" or "exception" or "err" || single.EndsWith("Exception", StringComparison.Ordinal) || single.EndsWith("Ex", StringComparison.Ordinal)))
            {
                return visited.WithExpression(access.WithName(IdentifierName(logMethod)))
                    .WithArgumentList(ParseArgumentList($"({single}, {single}.Message)").WithTriviaFrom(arguments));
            }

            if (arguments.Arguments.Count == 2 && method is "Error" or "Warn" or "Fatal" or "Debug" or "Info")
            {
                // Castle: Error(message, exception)  →  Microsoft: LogError(exception, message)
                arguments = arguments.WithArguments(SeparatedList(
                    [arguments.Arguments[1].WithoutTrivia(), arguments.Arguments[0].WithoutTrivia().WithLeadingTrivia(Space)],
                    arguments.Arguments.GetSeparators()));
            }

            return visited.WithExpression(access.WithName(IdentifierName(logMethod))).WithArgumentList(arguments);
        }

        // PermissionChecker.IsGrantedAsync(p) → AuthorizationService.IsGrantedAsync(p)
        if (receiver == "PermissionChecker")
        {
            var newMethod = method switch
            {
                "IsGrantedAsync" => "IsGrantedAsync",
                "AuthorizeAsync" => "CheckAsync",
                _ => null
            };

            if (newMethod is null)
            {
                Todo($"PermissionChecker.{method} → use AuthorizationService.IsGrantedAsync / CheckAsync (async only).");
                return visited;
            }

            Context.RequireUsing("Microsoft.AspNetCore.Authorization");
            Context.Change($"PermissionChecker.{method} → AuthorizationService.{newMethod}");
            return visited.WithExpression(SyntaxHelpers.Expr($"AuthorizationService.{newMethod}"));
        }

        // SettingManager.GetSettingValueAsync(name) → SettingProvider.GetOrNullAsync(name)
        // Boilerplate ISettingManager → ABP.io ISettingManager (Volo.Abp.SettingManagement) extension methods.
        if (receiver.EndsWith("settingManager", StringComparison.OrdinalIgnoreCase) && SettingManagerMethods.TryGetValue(method, out var settingMethod) &&
            access.Name is IdentifierNameSyntax)
        {
            Context.RequireUsing("Volo.Abp.SettingManagement");
            Context.Change($"SettingManager.{method} → {settingMethod.Name}");
            var args = visited.ArgumentList.Arguments.ToList();
            if (settingMethod.DropMiddleArgument && args.Count == 3) args.RemoveAt(1);
            var call = visited.WithExpression(access.WithName(IdentifierName(settingMethod.Name)))
                .WithArgumentList(visited.ArgumentList.WithArguments(SeparatedList(args, Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, args.Count - 1)))));
            if (settingMethod.Sync)
            {
                Todo($"SettingManager.{method} is synchronous; ABP.io settings are async — await {settingMethod.Name} instead of GetAwaiter().GetResult().");
                return SyntaxHelpers.Expr($"{call.WithoutTrivia()}.GetAwaiter().GetResult()").WithTriviaFrom(visited);
            }

            return call;
        }

        // LocalizationManager.GetString(source, key) / GetSource(source).GetString(key) → L[key]
        if (method == "GetString" && (receiver == "LocalizationManager" || receiver.StartsWith("LocalizationManager.GetSource(", StringComparison.Ordinal)))
        {
            var key = visited.ArgumentList.Arguments.Count switch
            {
                1 => visited.ArgumentList.Arguments[0].ToString(),
                >= 2 when receiver == "LocalizationManager" => visited.ArgumentList.Arguments[1].ToString(),
                _ => null
            };
            if (key is not null)
            {
                Context.Change("LocalizationManager.GetString → L[key]");
                return SyntaxHelpers.Expr($"L[{key}]").WithTriviaFrom(visited);
            }
        }

        // CurrentUnitOfWork.DisableFilter(AbpDataFilters.SoftDelete) → DataFilter.Disable<ISoftDelete>()
        if (method is "DisableFilter" or "EnableFilter" && (receiver is "CurrentUnitOfWork" or "UnitOfWorkManager.Current" or "_unitOfWorkManager.Current" ||
                                                           receiver.Contains("uow", StringComparison.OrdinalIgnoreCase) || receiver.EndsWith("UnitOfWork", StringComparison.OrdinalIgnoreCase)))
        {
            // Several filters that are one in ABP.io (MayHaveTenant + MustHaveTenant → IMultiTenant) become one Disable call.
            var interfaces = visited.ArgumentList.Arguments.Select(a => DataFilters.GetValueOrDefault(a.Expression.ToString().Split('.').Last())).Distinct().ToList();
            var filter = interfaces.Count == 1 && interfaces[0] is not null ? visited.ArgumentList.Arguments[0].Expression.ToString() : visited.ArgumentList.Arguments.Count == 1 ? visited.ArgumentList.Arguments[0].Expression.ToString() : "";
            var filterName = filter.Split('.').Last();
            if (DataFilters.TryGetValue(filterName, out var filterInterface))
            {
                Context.RequireUsing("Volo.Abp.Data");
                Context.RequireUsing("Volo.Abp");
                Context.RequireUsing("Volo.Abp.MultiTenancy");
                Context.Change($"{receiver}.{method}({filter}) → DataFilter.{method[..^6]}<{filterInterface}>()");
                if (!IsInsideServiceClass(node))
                {
                    Todo("DataFilter is available in ApplicationService/DomainService; inject IDataFilter elsewhere.");
                }

                return SyntaxHelpers.Expr($"DataFilter.{method[..^6]}<{filterInterface}>()").WithTriviaFrom(visited);
            }

            Todo($"Data filter '{filter}' → use IDataFilter.Disable<TFilter>() in ABP.io.");
            return visited;
        }

        if (method == "SetTenantId" && receiver.Contains("UnitOfWork", StringComparison.Ordinal))
        {
            Context.RequireUsing("Volo.Abp.MultiTenancy");
            Context.Change($"{receiver}.SetTenantId → CurrentTenant.Change");
            return visited.WithExpression(SyntaxHelpers.Expr("CurrentTenant.Change"));
        }

        // EventBus.TriggerAsync(e) → PublishAsync(e)
        if (receiver.Contains("EventBus", StringComparison.OrdinalIgnoreCase) && method is "TriggerAsync" or "Trigger")
        {
            if (method == "Trigger")
            {
                Todo("ILocalEventBus only has PublishAsync; await it from an async method.");
            }

            Context.Change($"EventBus.{method} → PublishAsync");
            var newReceiver = receiver == "EventBus" ? IdentifierName("LocalEventBus") : access.Expression;
            var published = visited.WithExpression(access.WithExpression(newReceiver).WithName(IdentifierName("PublishAsync")));
            // TriggerAsync(this, eventData): Boilerplate's first argument is the sender; PublishAsync only takes the event.
            return visited.ArgumentList.Arguments is [{ Expression: ThisExpressionSyntax }, var eventData]
                ? published.WithArgumentList(visited.ArgumentList.WithArguments(SingletonSeparatedList(eventData.WithoutLeadingTrivia())))
                : published;
        }

        return visited;
    }

    /// <summary>IEventHandler&lt;T&gt;.HandleEvent(T) (sync) → ILocalEventHandler&lt;T&gt;.HandleEventAsync(T).</summary>
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (visited.Identifier.Text != "HandleEvent" || visited.ParameterList.Parameters.Count != 1 ||
            node.Parent is not ClassDeclarationSyntax cls || !SyntaxHelpers.HasBaseType(cls, "IEventHandler"))
        {
            return visited;
        }

        Context.RequireUsing("System.Threading.Tasks");
        Context.Change("HandleEvent(e) → async Task HandleEventAsync(e)");
        var modifiers = visited.Modifiers;
        if (!modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            modifiers = modifiers.Add(Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space));
        }

        return visited
            .WithIdentifier(Id("HandleEventAsync", visited.Identifier))
            .WithModifiers(modifiers)
            .WithReturnType(IdentifierName("Task").WithTriviaFrom(visited.ReturnType));
    }

    private static bool IsInsideServiceClass(SyntaxNode node)
    {
        var cls = SyntaxHelpers.EnclosingClass(node);
        return cls?.BaseList?.Types.Any(t =>
        {
            var name = SyntaxHelpers.SimpleName(t.Type);
            return ServiceBaseSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)) || TypeMappings.Renames.ContainsKey(name);
        }) == true;
    }
}
