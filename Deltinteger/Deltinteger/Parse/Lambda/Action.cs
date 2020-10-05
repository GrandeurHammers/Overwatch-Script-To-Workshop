using System;
using System.Linq;
using System.Collections.Generic;
using Deltin.Deltinteger.Parse.FunctionBuilder;
using Deltin.Deltinteger.Compiler;
using Deltin.Deltinteger.Compiler.SyntaxTree;
using Deltin.Deltinteger.Elements;

namespace Deltin.Deltinteger.Parse.Lambda
{
    public class LambdaAction : IExpression, IWorkshopTree, IApplyBlock, IVariableTracker, ILambdaApplier
    {
        private readonly LambdaExpression _context;
        private readonly Scope _lambdaScope;
        private readonly ParseInfo _parseInfo;
        private readonly CodeType[] _argumentTypes;
        private readonly bool _isExplicit;

        /// <summary>The type of the lambda. This can either be BlockLambda, ValueBlockLambda, or MacroLambda.</summary>
        public PortableLambdaType LambdaType { get; private set; }

        /// <summary>The parameters of the lambda.</summary>
        public Var[] Parameters { get; }

        /// <summary>The invocation status of the lambda parameters/</summary>
        public SubLambdaInvoke[] InvokedState { get; }

        /// <summary>Determines if the lambda has multiple return statements. LambdaType will be ValueBlockLambda if this is true.</summary>
        public bool MultiplePaths { get; private set; }

        /// <summary>The block of the lambda. This will be null if LambdaType is MacroLambda.</summary>
        public IStatement Statement { get; private set; }

        /// <summary>The expression of the lambda. This will be null if LambdaType is BlockLambda or ValueBlockLambda.</summary>
        public IExpression Expression { get; private set; }

        /// <summary>The captured local variables.</summary>
        public List<IIndexReferencer> CapturedVariables { get; } = new List<IIndexReferencer>();

        /// <summary>The lambda's identifier.</summary>
        public int Identifier { get; set; }

        ///<summary> The parent type that the lambda is defined in.</summary>
        public CodeType This { get; }

        public CallInfo CallInfo { get; }
        public IRecursiveCallHandler RecursiveCallHandler { get; }

        public LambdaAction(ParseInfo parseInfo, Scope scope, LambdaExpression context)
        {
            _context = context;
            _lambdaScope = scope.Child();
            _parseInfo = parseInfo;
            RecursiveCallHandler = new LambdaRecursionHandler(this);
            CallInfo = new CallInfo(RecursiveCallHandler, parseInfo.Script);
            This = scope.GetThis();

            _isExplicit = context.Parameters.Any(p => p.Type != null);
            var parameterState = context.Parameters.Count == 0 || _isExplicit ? ParameterState.CountAndTypesKnown : ParameterState.CountKnown;

            // Get the lambda parameters.
            Parameters = new Var[context.Parameters.Count];
            InvokedState = new SubLambdaInvoke[Parameters.Length];
            _argumentTypes = new CodeType[Parameters.Length];

            for (int i = 0; i < Parameters.Length; i++)
            {
                if (_isExplicit && context.Parameters[i].Type == null)
                    parseInfo.Script.Diagnostics.Error("Inconsistent lambda parameter usage; parameter types must be all explicit or all implicit", context.Parameters[i].Range);

                InvokedState[i] = new SubLambdaInvoke();
                Parameters[i] = new ParameterVariable(_lambdaScope, new LambdaContextHandler(parseInfo, context.Parameters[i]), InvokedState[i]);
                _argumentTypes[i] = Parameters[i].CodeType;
            }

            new CheckLambdaContext(
                parseInfo,
                this,
                "Cannot determine lambda in the current context",
                context.Range,
                parameterState
            ).Check();

            // Add hover info
            // parseInfo.Script.AddHover(context.Arrow.Range, new MarkupBuilder().StartCodeLine().Add(LambdaType.GetName()).EndCodeLine().ToString());
        }

        public void GetLambdaStatement(PortableLambdaType expecting)
        {
            GetLambdaStatement();

            // Check if the current lambda implements the expected type.
            if (!LambdaType.Implements(expecting))
                _parseInfo.Script.Diagnostics.Error("Expected lambda of type '" + expecting.GetName() + "'", _context.Arrow.Range);
        }

        public void GetLambdaStatement()
        {
            ParseInfo parser = _parseInfo.SetCallInfo(CallInfo).SetVariableTracker(this);

            CodeType returnType = null;

            // Get the statements.
            if (_context.Statement is Block block)
            {
                // Parse the block.
                Statement = new BlockAction(parser, _lambdaScope, block);

                // Validate the block.
                BlockTreeScan validation = new BlockTreeScan(_parseInfo, (BlockAction)Statement, "lambda", _context.Arrow.Range);
                validation.ValidateReturns();

                if (validation.ReturnsValue)
                {
                    returnType = validation.ReturnType;
                    MultiplePaths = validation.MultiplePaths;
                }
            }
            else if (_context.Statement is ExpressionStatement exprStatement)
            {
                // Get the lambda expression.
                Expression = parser.GetExpression(_lambdaScope, exprStatement.Expression);
                returnType = Expression.Type();
            }
            else
            {
                // Statement
                Statement = parser.GetStatement(_lambdaScope, _context.Statement);
            }

            LambdaType = new PortableLambdaType(LambdaKind.Portable, _argumentTypes, returnType, _isExplicit);

            // Add so the lambda can be recursive-checked.
            _parseInfo.TranslateInfo.RecursionCheck(CallInfo);
            Identifier = _parseInfo.TranslateInfo.GetComponent<LambdaGroup>().Add(new LambdaHandler(this));
        }

        public IWorkshopTree Parse(ActionSet actionSet)
        {
            // If the lambda type is constant, return the lambda itself.
            if (LambdaType.IsConstant())
                return this;
            
            // Otherwise, return an array containing data of the lambda.
            var lambdaMeta = new List<IWorkshopTree>();

            // The first element is the lambda's identifier. 
            lambdaMeta.Add(new V_Number(Identifier));

            // The second element is the 'this' if applicable.
            lambdaMeta.Add(actionSet.This ?? new V_Null());

            // Every proceeding element is a captured local variable.
            foreach (var capture in CapturedVariables)
                lambdaMeta.Add(actionSet.IndexAssigner[capture].GetVariable());

            // Return the array.
            return Element.CreateArray(lambdaMeta.ToArray());
        }
        public Scope ReturningScope() => LambdaType.GetObjectScope();
        public CodeType Type() => LambdaType;

        public string ToWorkshop(OutputLanguage outputLanguage, ToWorkshopContext context) => throw new NotImplementedException();
        public bool EqualTo(IWorkshopTree other) => throw new NotImplementedException();

        public IWorkshopTree Invoke(ActionSet actionSet, params IWorkshopTree[] parameterValues)
        {
            switch (LambdaType.LambdaKind)
            {
                // Constant macro
                case LambdaKind.ConstantMacro:
                    return OutputConstantMacro(actionSet, parameterValues);
                
                // Constant block
                case LambdaKind.ConstantBlock:
                case LambdaKind.ConstantValue:
                    return OutputContantBlock(actionSet, parameterValues);
                
                // Portable
                case LambdaKind.Portable:
                    return OutputPortable(actionSet, parameterValues);
                
                // Unknown
                case LambdaKind.Anonymous:
                default:
                    throw new NotImplementedException();
            }
        }

        /// <summary>Assigns the parameter values to the action set for the constant lambdas.</summary>
        private ActionSet AssignContainedParameters(ActionSet actionSet, IWorkshopTree[] parameterValues)
        {
            var newSet = actionSet.New(actionSet.IndexAssigner.CreateContained());

            for (int i = 0; i < parameterValues.Length; i++)
                newSet.IndexAssigner.Add(Parameters[i], parameterValues[i]);
            
            return newSet;
        }
        /// <summary>Outputs a constant macro lambda.</summary>
        private IWorkshopTree OutputConstantMacro(ActionSet actionSet, IWorkshopTree[] parameterValues) => Expression.Parse(AssignContainedParameters(actionSet, parameterValues));

        /// <summary>Outputs a constant block.</summary>
        private IWorkshopTree OutputContantBlock(ActionSet actionSet, IWorkshopTree[] parameterValues)
        {
            ReturnHandler returnHandler = new ReturnHandler(actionSet, "lambda", MultiplePaths);
            Statement.Translate(AssignContainedParameters(actionSet, parameterValues).New(returnHandler));
            returnHandler.ApplyReturnSkips();
            
            return returnHandler.GetReturnedValue();
        }

        /// <summary>Outputs a portable lambda.</summary>
        private IWorkshopTree OutputPortable(ActionSet actionSet, IWorkshopTree[] parameterValues)
        {
            var determiner = actionSet.DeltinScript.GetComponent<LambdaGroup>();
            var builder = new FunctionBuildController(actionSet, new CallHandler(parameterValues), determiner);
            return builder.Call();
        }

        public string GetLabel(bool markdown)
        {
            string label = "";

            if (Parameters.Length == 1) label += Parameters[0].GetLabel(false);
            else
            {
                label += "(";
                for (int i = 0; i < Parameters.Length; i++)
                {
                    if (i != 0) label += ", ";
                    label += Parameters[i].GetLabel(false);
                }
                label += ")";
            }
            label += " => ";

            // if (LambdaType is MacroLambda macroLambda) label += macroLambda.ReturnType?.GetName() ?? "define";
            // else if (LambdaType is ValueBlockLambda vbl) label += "{" + (vbl.ReturnType?.GetName() ?? "define") + "}";
            // else if (LambdaType is BlockLambda) label += "{}";

            return label;
        }

        public void SetupParameters() {}
        public void SetupBlock() {}
        public void OnBlockApply(IOnBlockApplied onBlockApplied) => onBlockApplied.Applied();

        public void LocalVariableAccessed(IIndexReferencer variable)
        {
            if (!CapturedVariables.Contains(variable) && _lambdaScope.Parent.ScopeContains(variable))
                CapturedVariables.Add(variable);
        }

        public bool EmptyBlock => Statement == null || (Statement is BlockAction block && block.Statements.Length == 0);

        class LambdaRecursionHandler : IRecursiveCallHandler
        {
            public LambdaAction Lambda { get; }

            public LambdaRecursionHandler(LambdaAction lambda)
            {
                Lambda = lambda;
            }

            public CallInfo CallInfo => Lambda.CallInfo;
            public string TypeName => "lambda";
            public bool CanBeRecursivelyCalled() => false;
            public bool DoesRecursivelyCall(IRecursiveCallHandler calling) => calling is LambdaRecursionHandler lambdaRecursion && Lambda == lambdaRecursion.Lambda;
            public string GetLabel() => Lambda.GetLabel(false);
        }
    }
}