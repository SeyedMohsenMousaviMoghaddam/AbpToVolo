using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Type-level renames: IAbpSession → ICurrentUser, AsyncCrudAppService → CrudAppService (arity trimmed),
/// Entity → Entity&lt;int&gt;, IRepository&lt;T&gt; → IRepository&lt;T, int&gt;, CreatorUserId → CreatorId,
/// typeof(X).GetAssembly() → typeof(X).Assembly.
/// </summary>
public sealed class TypeNameRewriter : MigrationRewriter
{
    public TypeNameRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var name = node.Identifier.Text;
        // Both "x.Name" and the receiver "Name.x" are member usages, not type references
        // (e.g. ApplicationService.SettingProvider must not become SettingDefinitionProvider).
        var isMemberName = node.Parent is MemberAccessExpressionSyntax or ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax;

        if (TypeMappings.MemberRenames.TryGetValue(name, out var memberName))
        {
            Context.Change($"{name} → {memberName}");
            return node.WithIdentifier(Id(memberName, node.Identifier));
        }

        if (isMemberName || node.Parent is QualifiedNameSyntax || node.Ancestors().Any(a => a is UsingDirectiveSyntax))
        {
            return base.VisitIdentifierName(node);
        }

        if (TypeMappings.Renames.TryGetValue(name, out var sameName) && sameName.NewName == name)
        {
            // Same name in ABP.io, other namespace (ISettingManager, IBackgroundJobManager...): only the using changes.
            Context.RequireUsing(sameName.Namespace);
        }

        if (TypeMappings.Renames.TryGetValue(name, out var rename) && rename.NewName != name)
        {
            Context.RequireUsing(rename.Namespace);
            Context.Change($"{name} → {rename.NewName}");
            if (rename.Todo is not null) Todo(rename.Todo);
            return node.WithIdentifier(Id(rename.NewName, node.Identifier));
        }

        if (TypeMappings.IntKeyShortcuts.Contains(name) && IsTypePosition(node))
        {
            Context.Change($"{name} → {name}<int>");
            var identifier = Identifier(node.Identifier.LeadingTrivia, name, TriviaList());
            var typeArguments = TypeArgumentList(SingletonSeparatedList<TypeSyntax>(PredefinedType(Token(SyntaxKind.IntKeyword))))
                .WithTrailingTrivia(node.Identifier.TrailingTrivia);
            return GenericName(identifier, typeArguments);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
        var name = visited.Identifier.Text;
        var arguments = visited.TypeArgumentList.Arguments;

        if (TypeMappings.Renames.TryGetValue(name, out var rename))
        {
            Context.RequireUsing(rename.Namespace);
            if (rename.Todo is not null) Todo(rename.Todo);

            if (rename.MaxArity is { } max && arguments.Count > max)
            {
                Todo($"{name}: the extra generic arguments ({string.Join(", ", arguments.Skip(max))}) are not supported by ABP.io's {rename.NewName}.");
                arguments = SeparatedList(arguments.Take(max), arguments.GetSeparators().Take(max - 1));
            }

            if (rename.NewName != name || arguments.Count != visited.TypeArgumentList.Arguments.Count)
            {
                Context.Change($"{name}<...> → {rename.NewName}<...>");
            }

            visited = visited
                .WithIdentifier(Id(rename.NewName, visited.Identifier))
                .WithTypeArgumentList(visited.TypeArgumentList.WithArguments(arguments));
            name = rename.NewName;
            arguments = visited.TypeArgumentList.Arguments;
        }

        if (TypeMappings.AppendIntKeyWhenArity.TryGetValue(name, out var arity) && arguments.Count == arity)
        {
            Context.Change($"{name}<{arguments}> → {name}<{arguments}, int>");
            var withKey = arguments.Add(PredefinedType(Token(SyntaxKind.IntKeyword)));
            var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), withKey.Count - 1);
            visited = visited.WithTypeArgumentList(visited.TypeArgumentList.WithArguments(SeparatedList(withKey, separators)));
        }

        return visited;
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        // "public override long? CreatorUserId { get; set; }": ABP.io audit properties have protected setters and
        // are set by the framework; the override cannot compile and is removed.
        if (node.Modifiers.Any(SyntaxKind.OverrideKeyword) &&
            node.Identifier.Text is "CreatorUserId" or "LastModifierUserId" or "DeleterUserId" or "CreatorId" or "LastModifierId" or "DeleterId"
                or "CreationTime" or "LastModificationTime" or "DeletionTime" or "IsDeleted")
        {
            Context.Change($"Removed override of audit property {node.Identifier.Text} (managed by ABP.io)");
            Todo($"The override of {node.Identifier.Text} was removed; configure attributes such as [ForeignKey] in the DbContext model if needed.");
            return null;
        }

        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        if (TypeMappings.MemberRenames.TryGetValue(visited.Identifier.Text, out var newName))
        {
            visited = visited.WithIdentifier(Id(newName, visited.Identifier));
        }

        // ABP.io user and tenant ids are Guid: IMultiTenant.TenantId is Guid?, audit user ids are Guid?.
        var name = visited.Identifier.Text;
        var type = SyntaxHelpers.Text(visited.Type);
        var isTenantId = name == "TenantId" && type is "int" or "int?" &&
                         node.Parent is ClassDeclarationSyntax cls && SyntaxHelpers.HasBaseType(cls, "IMayHaveTenant", "IMustHaveTenant", "IMultiTenant");
        var isAuditUserId = name is "CreatorId" or "LastModifierId" or "DeleterId" && type is "long" or "long?";
        if (isTenantId || isAuditUserId)
        {
            Context.RequireUsing("System");
            Context.Change($"{name}: {type} → Guid?");
            return visited.WithType(NullableType(IdentifierName("Guid")).WithTriviaFrom(visited.Type));
        }

        return GuidId(name, visited.Type, entityProperty: IsEntityClass(node.Parent as ClassDeclarationSyntax)) is { } guid ? visited.WithType(guid) : visited;
    }

    /// <summary>
    /// An entity (its TenantId column follows the ABP.io standard, Guid): a multi-tenant or entity base type, or a class of
    /// the domain layer that is not a DTO/model.
    /// </summary>
    private bool IsEntityClass(ClassDeclarationSyntax? cls)
    {
        if (cls is null) return false;
        if (SyntaxHelpers.HasBaseType(cls, "IMayHaveTenant", "IMustHaveTenant", "IMultiTenant")) return true;
        if (cls.BaseList?.Types.Any(t => SyntaxHelpers.SimpleName(t.Type) is var b &&
                (b.Contains("Entity", StringComparison.Ordinal) || b.Contains("AggregateRoot", StringComparison.Ordinal) || b.Contains("Audited", StringComparison.Ordinal))) == true)
        {
            return true;
        }

        var name = cls.Identifier.Text;
        var dto = name.EndsWith("Dto", StringComparison.Ordinal) || name.EndsWith("Input", StringComparison.Ordinal) || name.EndsWith("Output", StringComparison.Ordinal) ||
                  name.EndsWith("Model", StringComparison.Ordinal) || name.EndsWith("Request", StringComparison.Ordinal) || name.EndsWith("Response", StringComparison.Ordinal);
        return Context.Project?.Layer == ProjectLayer.Domain && !dto;
    }

    /// <summary>
    /// User and tenant ids are Guid in ABP.io (Identity/TenantManagement). Variables, parameters, fields and properties
    /// named *TenantId (int) or *UserId (long) are converted, so the code stays consistent with CurrentTenant.Id,
    /// CurrentUser.Id and IdentityUser.Id. The database columns are migrated with Zero_IdMap (ModuleZeroDataMigration.sql).
    /// </summary>
    private TypeSyntax? GuidId(string name, TypeSyntax type, bool entityProperty = false)
    {
        var text = SyntaxHelpers.Text(type);
        if (ZeroMappings.GuidIdKind(name, text, Context.IdType) is not { } kind) return null;

        // --id-type source: only the entities' TenantId columns follow ABP.io (Guid); DTOs, models, parameters and variables
        // keep the int tenant ids of the source (the API does not change), converted with LegacyTenantIds where they meet.
        if (kind == "Tenant" && Context.IdType == IdTypeMode.Source && !entityProperty) return null;

        Context.RequireUsing("System");
        Context.Change($"{name}: {text} → Guid{(text.EndsWith('?') ? "?" : "")} (ABP.io {kind} ids are Guid)");
        TypeSyntax guid = IdentifierName("Guid");
        if (text.EndsWith('?')) guid = NullableType(guid);
        return guid.WithTriviaFrom(type);
    }

    /// <summary>
    /// Property names in strings follow the audit renames: [ForeignKey("CreatorUserId")], HasForeignKey("CreatorUserId"),
    /// HasIndex("CreatorUserId") → "CreatorId" (otherwise EF Core would create a shadow property with the old name).
    /// </summary>
    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.StringLiteralExpression) || !TypeMappings.MemberRenames.TryGetValue(node.Token.ValueText, out var renamed))
        {
            return base.VisitLiteralExpression(node);
        }

        var inForeignKey = node.Parent is AttributeArgumentSyntax { Parent.Parent: AttributeSyntax attribute } &&
                           SyntaxHelpers.SimpleName(attribute.Name) is "ForeignKey" or "ForeignKeyAttribute" or "Index" or "IndexAttribute";
        var inModelCall = node.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var method } } } &&
                          method is "HasForeignKey" or "HasIndex" or "Property" or "HasPrincipalKey" or "Ignore";
        if (!inForeignKey && !inModelCall) return base.VisitLiteralExpression(node);

        Context.Change($"\"{node.Token.ValueText}\" → \"{renamed}\" (ABP.io audit property name)");
        return LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(renamed)).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitParameter(ParameterSyntax node)
    {
        var visited = (ParameterSyntax)base.VisitParameter(node)!;
        return visited.Type is not null && GuidId(visited.Identifier.Text, visited.Type) is { } guid ? visited.WithType(guid) : visited;
    }

    public override SyntaxNode? VisitVariableDeclaration(VariableDeclarationSyntax node)
    {
        var visited = (VariableDeclarationSyntax)base.VisitVariableDeclaration(node)!;
        if (visited.Variables.Count != 1 || visited.Type.IsVar) return visited;
        return GuidId(visited.Variables[0].Identifier.Text, visited.Type) is { } guid ? visited.WithType(guid) : visited;
    }

    /// <summary>Boilerplate event classes derive from EventData; ABP.io events are plain classes.</summary>
    public override SyntaxNode? VisitBaseList(BaseListSyntax node)
    {
        var visited = (BaseListSyntax)base.VisitBaseList(node)!;
        var kept = visited.Types.Where(t => SyntaxHelpers.SimpleName(t.Type) is not ("EventData" or "IEventData")).ToList();
        if (kept.Count == visited.Types.Count) return visited;

        Context.Change("Removed the EventData base class (ABP.io event types are plain classes)");
        if (kept.Count == 0) return null;
        var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), kept.Count - 1);
        return visited.WithTypes(SeparatedList(kept.Select((t, i) => i == 0 ? t.WithLeadingTrivia(Space) : t), separators));
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (visited is { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAssembly" } access, ArgumentList.Arguments.Count: 0 })
        {
            Context.Change(".GetAssembly() → .Assembly");
            return access.WithName(IdentifierName("Assembly")).WithTriviaFrom(visited);
        }

        return visited;
    }

    private static bool IsTypePosition(IdentifierNameSyntax node) => node.Parent switch
    {
        SimpleBaseTypeSyntax or TypeArgumentListSyntax or TypeOfExpressionSyntax or NullableTypeSyntax or ArrayTypeSyntax or TypeConstraintSyntax => true,
        VariableDeclarationSyntax v => v.Type == node,
        ParameterSyntax p => p.Type == node,
        PropertyDeclarationSyntax p => p.Type == node,
        MethodDeclarationSyntax m => m.ReturnType == node,
        ObjectCreationExpressionSyntax o => o.Type == node,
        CastExpressionSyntax c => c.Type == node,
        _ => false
    };
}
