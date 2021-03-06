using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CodeAnnotations;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.DeclaredElements;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Resolve;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.Util;
using ReSharperPlugin.HeapView.Highlightings;

// ReSharper disable RedundantExplicitParamsArrayCreation

namespace ReSharperPlugin.HeapView.Analyzers
{
  [ElementProblemAnalyzer(
    new[] {
      typeof(IReferenceExpression),
      typeof(IObjectCreationExpression),
      typeof(IAnonymousObjectCreationExpression),
      typeof(IArrayCreationExpression),
      typeof(IInvocationExpression),
      typeof(IArrayInitializer),
      typeof(IForeachStatement),
      typeof(IAdditiveExpression),
      typeof(IAssignmentExpression),
      typeof(IElementAccessExpression),
      typeof(IConstructorInitializer),
      typeof(ICollectionElementInitializer)
    },
    HighlightingTypes = new[] {
      typeof(ObjectAllocationHighlighting),
      typeof(ObjectAllocationEvidentHighlighting),
      typeof(ObjectAllocationPossibleHighlighting),
      typeof(DelegateAllocationHighlighting)
    })]
  public sealed class HeapAllocationAnalyzer : ElementProblemAnalyzer<ITreeNode>
  {
    protected override void Run(ITreeNode element, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
    {
      switch (element)
      {
        // var t = new object();
        case IObjectCreationExpression objectCreation:
          CheckObjectCreation(objectCreation, consumer);
          CheckInvocationInfo(objectCreation, objectCreation.TypeName, consumer);
          return;

        // var t = new { Foo = 123 };
        case IAnonymousObjectCreationExpression anonymousObjectCreation:
          CheckAnonymousObjectCreation(anonymousObjectCreation, consumer);
          return;

        // var xs = new[] {1, 2, 3};
        case IArrayCreationExpression arrayCreation:
          CheckArrayCreation(arrayCreation, consumer);
          return;

        // int[] xs = {1, 2, 3};
        case IArrayInitializer arrayInitializer:
          CheckArrayInitializer(arrayInitializer, consumer);
          return;

        // F(); when F(params T[] xs);
        // F(); when F is iterator
        case IInvocationExpression invocationExpression:
          CheckInvocationExpression(invocationExpression, consumer);
          return;

        // Action f = F;
        // var xs = Iterator;
        case IReferenceExpression referenceExpression:
          CheckReferenceExpression(referenceExpression, consumer);
          return;

        // string s = "abc" + x + "def";
        case IAdditiveExpression additiveExpression:
          CheckStringConcatenation(additiveExpression, consumer);
          return;

        // str += "abc";
        case IAssignmentExpression assignmentExpression when assignmentExpression.IsCompoundAssignment
                                                             && IsStringConcatenation(assignmentExpression):
          consumer.AddHighlighting(
            new ObjectAllocationHighlighting(assignmentExpression.OperatorSign, "string concatenation"),
            assignmentExpression.OperatorSign.GetDocumentRange());
          break;

        // foreach (var x in xs); when xs.GetEnumerator() is ref-type
        // note: produces false-positive for LocalList<T>-produced IList<T>
        case IForeachStatement foreachStatement:
          CheckForeachDeclaration(foreachStatement, consumer);
          return;

        // ["params", "arg"]
        case IElementAccessExpression elementAccessExpression:
          CheckInvocationInfo(elementAccessExpression, null, consumer);
          return;

        // : base("params", "arg")
        case IConstructorInitializer constructorInitializer:
          CheckInvocationInfo(constructorInitializer, constructorInitializer.Instance, consumer);
          return;

        // new C { {"params", "arg"} }
        case ICollectionElementInitializer collectionElementInitializer:
          CheckInvocationInfo(collectionElementInitializer, null, consumer);
          return;
      }
    }

    private static void CheckObjectCreation([NotNull] IObjectCreationExpression objectCreation, [NotNull] IHighlightingConsumer consumer)
    {
      var typeReference = objectCreation.TypeReference;
      if (typeReference == null) return;

      if (IsIgnoredContext(objectCreation)) return;

      var newKeyword = objectCreation.NewKeyword.NotNull();

      var typeElement = typeReference.Resolve().DeclaredElement as ITypeElement;
      var typeParameter = typeElement as ITypeParameter;

      if (typeElement is IClass || typeParameter != null && typeParameter.IsReferenceType)
      {
        consumer.AddHighlighting(
          new ObjectAllocationEvidentHighlighting(newKeyword, "reference type creation"),
          newKeyword.GetDocumentRange());
      }
      else if (typeParameter != null && !typeParameter.IsValueType)
      {
        consumer.AddHighlighting(
          new ObjectAllocationPossibleHighlighting(newKeyword, "reference type creation"),
          newKeyword.GetDocumentRange());
      }
    }

    private static void CheckAnonymousObjectCreation([NotNull] ICreationExpression objectCreation, [NotNull] IHighlightingConsumer consumer)
    {
      var newKeyword = objectCreation.NewKeyword.NotNull();

      consumer.AddHighlighting(
        new ObjectAllocationEvidentHighlighting(newKeyword, "reference type instantiation"),
        newKeyword.GetDocumentRange());
    }

    private static void CheckArrayCreation([NotNull] IArrayCreationExpression arrayCreation, [NotNull] IHighlightingConsumer consumer)
    {
      if (IsIgnoredContext(arrayCreation)) return;

      var newKeyword = arrayCreation.NewKeyword.NotNull();

      consumer.AddHighlighting(
        new ObjectAllocationEvidentHighlighting(newKeyword, "array creation"),
        newKeyword.GetDocumentRange());
    }

    private static void CheckArrayInitializer([NotNull] IArrayInitializer arrayInitializer, [NotNull] IHighlightingConsumer consumer)
    {
      ITreeNode start = null, end = null;
      var variableDeclaration = LocalVariableDeclarationNavigator.GetByInitial(arrayInitializer);
      if (variableDeclaration?.EquivalenceSign != null)
      {
        start = variableDeclaration.NameIdentifier;
        end = variableDeclaration.EquivalenceSign;
      }
      else
      {
        var fieldDeclaration = FieldDeclarationNavigator.GetByInitial(arrayInitializer);
        if (fieldDeclaration?.EquivalenceSign != null)
        {
          start = fieldDeclaration.NameIdentifier;
          end = fieldDeclaration.EquivalenceSign;
        }
      }

      if (start != null && end != null)
      {
        var endOffset = end.GetDocumentEndOffset();
        var highlighting = new ObjectAllocationEvidentHighlighting(arrayInitializer, "array instantiation");
        var documentRange = start.GetDocumentRange().SetEndTo(endOffset);

        consumer.AddHighlighting(highlighting, documentRange);
      }
    }

    private static void CheckInvocationExpression([NotNull] IInvocationExpression invocation, [NotNull] IHighlightingConsumer consumer)
    {
      var invokedExpression = invocation.InvokedExpression;
      if (invokedExpression == null) return;

      CheckInvocationInfo(invocation, invokedExpression as IReferenceExpression, consumer);

      var invocationReference = invocation.InvocationExpressionReference.NotNull("reference != null");

      var (declaredElement, _, resolveErrorType) = invocationReference.Resolve();
      if (resolveErrorType != ResolveErrorType.OK) return;

      var method = declaredElement as IMethod;
      if (method == null) return;

      if (method.IsIterator)
      {
        consumer.AddHighlighting(
          new ObjectAllocationHighlighting(invocation, "iterator method call"),
          invokedExpression.GetExpressionRange());
      }
      else if (method.ReturnType.Classify == TypeClassification.REFERENCE_TYPE)
      {
        var annotationsCache = invocation.GetPsiServices().GetCodeAnnotationsCache();
        var linqTunnelAnnotationProvider = annotationsCache.GetProvider<LinqTunnelAnnotationProvider>();
        var pureAnnotationProvider = annotationsCache.GetProvider<PureAnnotationProvider>();

        if (pureAnnotationProvider.GetInfo(method) && linqTunnelAnnotationProvider.GetInfo(method))
        {
          consumer.AddHighlighting(
            new ObjectAllocationHighlighting(invocation, "LINQ method call"),
            invokedExpression.GetExpressionRange());
        }
      }
    }

    private static void CheckInvocationInfo(
      [NotNull] ICSharpInvocationInfo invocationInfo, [CanBeNull] ITreeNode invocationAnchor, [NotNull] IHighlightingConsumer consumer)
    {
      var invocationReference = invocationInfo.Reference;
      if (invocationReference == null) return;

      var (declaredElement, _, resolveErrorType) = invocationReference.Resolve();

      var parametersOwner = declaredElement as IParametersOwner;
      if (parametersOwner == null) return;

      if (resolveErrorType != ResolveErrorType.OK) return;

      var parameters = parametersOwner.Parameters;
      if (parameters.Count == 0) return;

      var lastParameter = parameters[parameters.Count - 1];
      if (!lastParameter.IsParameterArray) return;

      ICSharpExpression paramsArgument = null;
      foreach (var argumentInfo in invocationInfo.Arguments)
      {
        var parameter = argumentInfo.MatchingParameter;
        if (parameter == null) continue;

        if (!Equals(parameter.Element, lastParameter)) continue;

        // found explicit array pass
        if (parameter.Expanded == ArgumentsUtil.ExpandedKind.None) return;

        if (argumentInfo is ICSharpArgument argument)
        {
          paramsArgument = argument.Value;
        }

        break;
      }

      var anchor = invocationAnchor ?? paramsArgument ?? invocationInfo as ICSharpExpression;
      if (anchor == null) return;

      if (IsCachedEmptyArrayAvailable(anchor)) return;

      var expression = anchor as ICSharpExpression;
      var paramsRange = expression?.GetExpressionRange() ?? anchor.GetDocumentRange();

      var description = $"parameters array '{lastParameter.ShortName}' creation";
      consumer.AddHighlighting(new ObjectAllocationHighlighting(anchor, description), paramsRange);
    }

    private static void CheckReferenceExpression([NotNull] IReferenceExpression referenceExpression, [NotNull] IHighlightingConsumer consumer)
    {
      var (declaredElement, _) = referenceExpression.Reference.Resolve();

      switch (declaredElement)
      {
        case IProperty { Getter: IAccessor getter }:
        {
          var languageService = referenceExpression.Language.LanguageServiceNotNull();

          var accessType = languageService.GetReferenceAccessType(referenceExpression.Reference);
          if (accessType == ReferenceAccessType.READ && getter.IsIterator)
          {
            consumer.AddHighlighting(
              new ObjectAllocationHighlighting(referenceExpression, "iterator property access"),
              referenceExpression.NameIdentifier.GetDocumentRange());
          }

          break;
        }

        case IMethod _:
        case ILocalFunction _:
        {
          // todo: check inside delegate invocation

          var convertedTo = referenceExpression.GetImplicitlyConvertedTo();
          if (convertedTo is IDeclaredType (IDelegate _, _))
          {
            consumer.AddHighlighting(
              new DelegateAllocationHighlighting(referenceExpression, "from method group"),
              referenceExpression.NameIdentifier.GetDocumentRange());
          }

          break;
        }
      }
    }

    private static void CheckStringConcatenation([NotNull] IAdditiveExpression concatenation, [NotNull] IHighlightingConsumer consumer)
    {
      if (!IsStringConcatenation(concatenation)) return;

      // do not inspect inner concatenations
      var parent = concatenation.GetContainingParenthesizedExpression();
      var parentConcatenation = AdditiveExpressionNavigator.GetByLeftOperand(parent)
                            ?? AdditiveExpressionNavigator.GetByRightOperand(parent);
      if (parentConcatenation != null && IsStringConcatenation(parentConcatenation)) return;

      // collect all operands
      var allConstants = true;
      var concatenations = new List<IAdditiveExpression>();
      if (CollectStringConcatenation(concatenation, concatenations, ref allConstants)
          && !allConstants
          && concatenations.Count > 0)
      {
        var mostLeftConcatSign = concatenations
          .Select(x => x.OperatorSign.GetDocumentRange())
          .OrderBy(x => x.TextRange.StartOffset).First();

        var operandsCount = concatenations.Count + 1;
        var description = "string concatenation"
          + (operandsCount <= 2 ? null : $" ({operandsCount} operands)")
          + (operandsCount <= 4 ? null : " + params array allocation");

        consumer.AddHighlighting(
          new ObjectAllocationHighlighting(concatenation, description),
          mostLeftConcatSign);
      }
    }

    private static bool CollectStringConcatenation(
      [NotNull] IAdditiveExpression concatenation, [NotNull] List<IAdditiveExpression> parts, ref bool allConstants)
    {
      var lhsOperand = concatenation.LeftOperand;
      var rhsOperand = concatenation.RightOperand;
      if (lhsOperand == null || rhsOperand == null ||
          concatenation.OperatorSign == null) return false;

      var lhsConstant = lhsOperand.ConstantValue;
      if (!lhsConstant.IsString() && !lhsConstant.IsPureNull(concatenation.Language))
      {
        allConstants = false;

        if (lhsOperand.GetOperandThroughParenthesis() is IAdditiveExpression left && IsStringConcatenation(concatenation))
        {
          if (!CollectStringConcatenation(left, parts, ref allConstants)) return false;
        }
      }

      var rhsConstant = rhsOperand.ConstantValue;
      if (!rhsConstant.IsString() && !rhsConstant.IsPureNull(concatenation.Language))
      {
        allConstants = false;

        if (rhsOperand.GetOperandThroughParenthesis() is IAdditiveExpression right && IsStringConcatenation(concatenation))
        {
          if (!CollectStringConcatenation(right, parts, ref allConstants)) return false;
        }
      }

      parts.Add(concatenation);
      return true;
    }

    private static bool IsStringConcatenation([NotNull] IAdditiveExpression concatenation)
    {
      var leftOperand = concatenation.LeftOperand;
      if (leftOperand == null) return false;

      var rightOperand = concatenation.RightOperand;
      if (rightOperand == null) return false;

      return IsStringConcatOperatorReference(concatenation.OperatorReference);
    }

    private static bool IsStringConcatenation([NotNull] IAssignmentExpression concatenation)
    {
      var sourceOperand = concatenation.Source;
      if (sourceOperand == null) return false;

      var destinationOperand = concatenation.Dest;
      if (destinationOperand == null) return false;

      return IsStringConcatOperatorReference(concatenation.OperatorReference);
    }

    private static bool IsStringConcatOperatorReference([CanBeNull] IReference reference)
    {
      var @operator = reference?.Resolve().DeclaredElement as IOperator;
      if (@operator == null || !@operator.IsPredefined) return false;

      var parameters = @operator.Parameters;
      if (parameters.Count != 2) return false;

      IType lhsType = parameters[0].Type, rhsType = parameters[1].Type;
      if (lhsType.IsString()) return rhsType.IsString() || rhsType.IsObject();
      if (rhsType.IsString()) return lhsType.IsString() || lhsType.IsObject();

      return false;
    }

    private static void CheckForeachDeclaration([NotNull] IForeachStatement foreachStatement, [NotNull] IHighlightingConsumer consumer)
    {
      var collection = foreachStatement.Collection;

      var collectionType = collection?.Type() as IDeclaredType;
      if (collectionType == null || collectionType.IsUnknown) return;

      // no allocations because of compiler support (just like arrays)
      if (collectionType.IsString()) return;

      var typeElement = collectionType.GetTypeElement();
      if (typeElement == null) return;

      // search for GetEnumerator() method
      var symbolTable = ResolveUtil.GetSymbolTableByTypeElement(typeElement, SymbolTableMode.FULL, typeElement.Module);

      foreach (var symbolInfo in symbolTable.GetSymbolInfos("GetEnumerator"))
      {
        var method = symbolInfo.GetDeclaredElement() as IMethod;
        // ReSharper disable once UseNullPropagation
        if (method == null) continue;

        if (!CSharpDeclaredElementUtil.IsForeachEnumeratorPatternMember(method)) continue;

        // with ref-return
        if (method.ReturnType.Classify == TypeClassification.REFERENCE_TYPE)
        {
          DocumentRange range;
          var inToken = collection.GetPreviousMeaningfulToken();
          if (inToken != null && inToken.GetTokenType().IsKeyword)
            range = inToken.GetDocumentRange();
          else
            range = collection.GetExpressionRange();

          var highlighting = new ObjectAllocationPossibleHighlighting(
            foreachStatement, "enumerator allocation (except iterators or collection with cached enumerator)");
          consumer.AddHighlighting(highlighting, range);
        }

        break;
      }
    }

    [Pure]
    public static bool IsIgnoredContext([NotNull] ITreeNode context)
    {
      // todo: static context?

      var attribute = context.GetContainingNode<IAttribute>();
      if (attribute != null) return true;

      return false;
    }

    private static bool IsCachedEmptyArrayAvailable([NotNull] ITreeNode context)
    {
      if (!context.IsCSharp6Supported()) return false;

      var systemArrayType = context.GetPredefinedType().Array;
      var classType = systemArrayType.GetTypeElement() as IClass;
      if (classType == null) return false;

      foreach (var typeMember in classType.EnumerateMembers("Empty", caseSensitive: false))
      {
        var method = typeMember as IMethod;
        if (method?.Parameters.Count == 0
          && method.TypeParameters.Count == 1
          && method.GetAccessRights() == AccessRights.PUBLIC) return true;
      }

      return false;
    }
  }
}