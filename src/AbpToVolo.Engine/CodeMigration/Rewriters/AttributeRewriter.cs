using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// [AbpAuthorize] → [Authorize], [AbpAllowAnonymous] → [AllowAnonymous],
/// [AutoMapFrom]/[AutoMapTo]/[AutoMap] → AutoMapper's [AutoMap], [DontWrapResult]/[WrapResult] removed.
/// </summary>
public sealed class AttributeRewriter : MigrationRewriter
{
    private static readonly HashSet<string> RemovedAttributes = new(StringComparer.Ordinal)
    {
        "DontWrapResult", "WrapResult"
    };

    public AttributeRewriter(FileMigrationContext context) : base(context)
    {
    }

    private static string Name(AttributeSyntax attribute)
    {
        var name = SyntaxHelpers.SimpleName(attribute.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
    {
        var visited = (AttributeListSyntax)base.VisitAttributeList(node)!;
        var attributes = new List<AttributeSyntax>();
        var changed = false;

        foreach (var attribute in visited.Attributes)
        {
            var name = Name(attribute);
            switch (name)
            {
                case var _ when RemovedAttributes.Contains(name):
                    Context.Change($"[{name}] removed (ABP.io does not wrap results)");
                    changed = true;
                    break;

                case "AbpAuthorize" or "AbpMvcAuthorize":
                    attributes.Add(ConvertAuthorize(attribute));
                    changed = true;
                    break;

                case "AbpAllowAnonymous":
                    Context.RequireUsing("Microsoft.AspNetCore.Authorization");
                    Context.Change("[AbpAllowAnonymous] → [AllowAnonymous]");
                    attributes.Add(attribute.WithName(IdentifierName("AllowAnonymous").WithTriviaFrom(attribute.Name)));
                    changed = true;
                    break;

                case "AutoMapFrom" or "AutoMapTo" or "AutoMap":
                    attributes.AddRange(ConvertAutoMap(attribute, name));
                    changed = true;
                    break;

                default:
                    attributes.Add(attribute);
                    break;
            }
        }

        if (!changed)
        {
            return visited;
        }

        if (attributes.Count == 0)
        {
            return null; // the whole [..] list disappears
        }

        var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), attributes.Count - 1);
        return visited.WithAttributes(SeparatedList(attributes, separators));
    }

    private AttributeSyntax ConvertAuthorize(AttributeSyntax attribute)
    {
        Context.RequireUsing("Microsoft.AspNetCore.Authorization");
        Context.Change("[AbpAuthorize] → [Authorize]");
        var converted = attribute.WithName(IdentifierName("Authorize").WithTriviaFrom(attribute.Name));
        var arguments = attribute.ArgumentList?.Arguments ?? default;

        if (arguments.Count > 1)
        {
            Todo($"[Authorize] accepts a single policy; the other permissions ({string.Join(", ", arguments.Skip(1))}) must be checked " +
                 "with additional [Authorize] attributes (all required) or AuthorizationService.IsGrantedAnyAsync (any).");
            converted = converted.WithArgumentList(attribute.ArgumentList!.WithArguments(SingletonSeparatedList(arguments[0])));
        }

        return converted;
    }

    private IEnumerable<AttributeSyntax> ConvertAutoMap(AttributeSyntax attribute, string name)
    {
        Context.RequireUsing("AutoMapper");
        Context.Change($"[{name}] → AutoMapper [AutoMap]");
        var types = attribute.ArgumentList?.Arguments.Select(a => a.Expression.ToString()).ToList() ?? [];
        var reverse = name is "AutoMapTo" or "AutoMap" ? ", ReverseMap = true" : string.Empty;

        if (types.Count == 0)
        {
            Todo($"[{name}] without types; create an AutoMapper Profile instead.");
            yield return attribute;
            yield break;
        }

        foreach (var type in types)
        {
            yield return Attribute(IdentifierName("AutoMap"), ParseAttributeArgumentList($"({type}{reverse})"));
        }
    }
}
