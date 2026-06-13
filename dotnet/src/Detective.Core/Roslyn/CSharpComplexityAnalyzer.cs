using Detective.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Detective.Core.Roslyn;

/// <summary>
/// Roslyn-powered code metrics for C#. Replaces the TypeScript-compiler based
/// <c>complexity.ts</c>/x-ray analyzers from the original tool. Cyclomatic
/// complexity counts decision points (if/while/for/foreach/case/catch, the
/// ternary and switch-expression arms, and the <c>&amp;&amp;</c>/<c>||</c> operators).
/// </summary>
public static class CSharpComplexityAnalyzer
{
    public static int CyclomaticComplexity(string sourceCode)
    {
        var root = CSharpSyntaxTree.ParseText(sourceCode).GetRoot();
        return CyclomaticComplexity(root);
    }

    public static int CyclomaticComplexity(SyntaxNode node)
    {
        var complexity = 1;
        foreach (var n in node.DescendantNodesAndSelf())
        {
            switch (n)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                    complexity++;
                    break;
                case BinaryExpressionSyntax b
                    when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression):
                    complexity++;
                    break;
            }
        }
        return complexity;
    }

    /// <summary>Rich per-file metrics used by the x-ray view.</summary>
    public static CodeMetrics FileMetrics(string sourceCode, string filePath)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        var methods = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().ToList();
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

        var maxMethodLength = 0;
        foreach (var method in methods)
        {
            var span = method.GetLocation().GetLineSpan();
            var len = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            if (len > maxMethodLength) maxMethodLength = len;
        }

        return new CodeMetrics
        {
            FilePath = filePath,
            Lines = CountLines(sourceCode),
            CyclomaticComplexity = CyclomaticComplexity(root),
            MethodCount = methods.Count,
            TypeCount = types.Count,
            MaxNestingDepth = MaxNestingDepth(root),
            MaxMethodLength = maxMethodLength,
        };
    }

    public static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var lines = 1;
        foreach (var c in text) if (c == '\n') lines++;
        return lines;
    }

    private static int MaxNestingDepth(SyntaxNode root)
    {
        var max = 0;
        void Walk(SyntaxNode node, int depth)
        {
            if (depth > max) max = depth;
            foreach (var child in node.ChildNodes())
            {
                var isNesting = child is BlockSyntax
                    && child.Parent is IfStatementSyntax or ElseClauseSyntax or ForStatementSyntax
                        or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                        or SwitchStatementSyntax or TryStatementSyntax or CatchClauseSyntax
                        or UsingStatementSyntax or LockStatementSyntax;
                Walk(child, isNesting ? depth + 1 : depth);
            }
        }
        Walk(root, 0);
        return max;
    }
}
