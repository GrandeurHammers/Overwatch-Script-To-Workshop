using System;
using System.Collections.Generic;
using Deltin.Deltinteger.LanguageServer;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.Compiler;
using Deltin.Deltinteger.Compiler.SyntaxTree;

namespace Deltin.Deltinteger.Parse
{
    public abstract class LoopAction : IStatement, IBlockContainer, IContinueContainer, IBreakContainer
    {
        /// <summary>The path info of the loop block.</summary>
        protected PathInfo Path;

        /// <summary>Determines if the continue action is used directly.</summary>
        protected bool RawContinue = true;
        /// <summary>Determines if the break action is used directly.</summary>
        protected readonly bool RawBreak = true; // Remove the readonly if this needs to be changed.

        /// <summary>Stores skips that continue the loop.</summary>
        private readonly List<SkipStartMarker> Continue = new List<SkipStartMarker>();

        /// <summary>Stores skips that break the loop.</summary>
        private readonly List<SkipStartMarker> Break = new List<SkipStartMarker>();

        public abstract void Translate(ActionSet actionSet);

        public PathInfo[] GetPaths() => new PathInfo[] { Path };

        public void AddContinue(ActionSet actionSet, string comment)
        {
            if (RawContinue)
                actionSet.AddAction(new A_Continue() { Comment = comment });
            else
            {
                SkipStartMarker continuer = new SkipStartMarker(actionSet, comment);
                actionSet.AddAction(continuer);
                Continue.Add(continuer);
            }
        }

        public void AddBreak(ActionSet actionSet, string comment)
        {
            if (RawBreak)
                actionSet.AddAction(new A_Break() { Comment = comment });
            else
            {
                SkipStartMarker breaker = new SkipStartMarker(actionSet, comment);
                actionSet.AddAction(breaker);
                Break.Add(breaker);
            }
        }

        protected void ResolveContinues(ActionSet actionSet)
        {
            Resolve(actionSet, Continue);
        }
        protected void ResolveBreaks(ActionSet actionSet)
        {
            Resolve(actionSet, Break);
        }
        private void Resolve(ActionSet actionSet, List<SkipStartMarker> skips)
        {
            // Create the end marker that marks the spot right before the End action (if continuing) or right after the End action (if breaking).
            SkipEndMarker endMarker = new SkipEndMarker();

            // Add the end marker to the action set.
            actionSet.AddAction(endMarker);

            // Assign the end marker to the continue/break skips.
            foreach (SkipStartMarker startMarker in skips)
                startMarker.SetEndMarker(endMarker);
        }
    }

    class WhileAction : LoopAction
    {
        private IExpression Condition { get; }
        private IStatement Block { get; }

        public WhileAction(ParseInfo parseInfo, Scope scope, While whileContext)
        {
            RawContinue = true;
            Condition = parseInfo.GetExpression(scope, whileContext.Condition);

            Block = parseInfo.SetLoop(this).GetStatement(scope, whileContext.Statement);
            Path = new PathInfo(Block, whileContext.Range, false);
        }

        public override void Translate(ActionSet actionSet)
        {
            int actionCountPreCondition = actionSet.ActionCount;

            Element condition = (Element)Condition.Parse(actionSet);
            bool actionsAdded = actionSet.ActionCount > actionCountPreCondition;

            if (!actionsAdded)
            {
                // Create a normal while loop.
                actionSet.AddAction(Element.Part<A_While>(condition));

                // Translate the block.
                Block.Translate(actionSet.Indent());

                // Resolve continues.
                ResolveContinues(actionSet);

                // Cap the block.
                actionSet.AddAction(new A_End());

                // Resolve breaks.
                ResolveBreaks(actionSet);
            }
            else
            {
                // The while condition requires actions to get the value.
                actionSet.ActionList.Insert(actionCountPreCondition, new ALAction(Element.Part<A_While>(new V_True())));

                SkipStartMarker whileEndSkip = new SkipStartMarker(actionSet, condition);
                actionSet.Indent().AddAction(whileEndSkip);

                // Translate the block.
                Block.Translate(actionSet.Indent());

                // Resolve continues.
                ResolveContinues(actionSet);

                // Cap the block.
                actionSet.AddAction(new A_End());

                // Skip to the end when the condition is false.
                SkipEndMarker whileEnd = new SkipEndMarker();
                whileEndSkip.SetEndMarker(whileEnd);
                actionSet.AddAction(whileEnd);

                // Resolve breaks.
                ResolveBreaks(actionSet);
            }
        }
    }

    class ForAction : LoopAction
    {
        readonly bool IsAutoFor;
        readonly IStatement Block;
        readonly IExpression Condition;
        readonly Var DefinedVariable;

        // For
        readonly SetVariableAction Initializer;
        readonly IStatement Iterator;

        // Auto-for
        readonly VariableResolve VariableResolve;
        readonly IExpression InitialResolveValue;
        readonly IExpression Step;

        public ForAction(ParseInfo parseInfo, Scope scope, For forContext)
        {
            Scope varScope = scope.Child();

            IsAutoFor = forContext.Iterator is ExpressionStatement;

            // Get the initializer.
            if (!IsAutoFor)
            {
                // Normal for loop initializer.
                if (forContext.Initializer != null)
                {
                    // Declaration for initializer.
                    if (forContext.Initializer is VariableDeclaration declaration)
                        DefinedVariable = new ScopedVariable(varScope, new DefineContextHandler(parseInfo, declaration));
                    // Variable assignment for initializer
                    else if (forContext.Initializer is Assignment assignment)
                        Initializer = new SetVariableAction(parseInfo, varScope, assignment);

                    // TODO: Throw error on incorrect initializer type.
                }
            }
            else
            {
                // Auto-for initializer.
                // Missing initializer.
                if (forContext.Initializer == null)
                {
                    // Error if there is no initializer.
                    if (forContext.InitializerSemicolon)
                        parseInfo.Script.Diagnostics.Error("Auto-for loops require an initializer.", forContext.InitializerSemicolon.Range);
                }
                // Declaration
                else if (forContext.Initializer is VariableDeclaration declaration)
                {
                    DefinedVariable = new ScopedVariable(varScope, new DefineContextHandler(parseInfo, declaration));
                }
                // Assignment
                else if (forContext.Initializer is Assignment assignment)
                {
                    // Get the variable being set.
                    VariableResolve = new VariableResolve(new VariableResolveOptions()
                    {
                        // The for cannot be indexed and should be on the rule-level.
                        CanBeIndexed = false,
                        FullVariable = true
                    }, parseInfo.GetExpression(varScope, assignment.VariableExpression), assignment.VariableExpression.Range, parseInfo.Script.Diagnostics);

                    InitialResolveValue = parseInfo.GetExpression(scope, assignment.Value);
                }
                // Variable
                else if (forContext.Initializer is ExpressionStatement exprStatement && exprStatement.Expression is Identifier identifier)
                {
                    // The variable is defined but no start value was given. In this case, just start at 0.
                    // Get the variable.
                    VariableResolve = new VariableResolve(new VariableResolveOptions()
                    {
                        // The for cannot be indexed and should be on the rule-level.
                        CanBeIndexed = false,
                        FullVariable = true
                    }, parseInfo.GetExpression(varScope, identifier), identifier.Range, parseInfo.Script.Diagnostics);
                }
                // Incorrect initializer.
                else
                {
                    // TODO: throw error on incorrect expression type.
                }
            }

            // Get the condition.
            if (forContext.Condition != null)
                Condition = parseInfo.GetExpression(varScope, forContext.Condition);

            // Get the iterator.
            if (forContext.Iterator != null)
            {
                // Get the auto-for
                if (IsAutoFor)
                {
                    Step = parseInfo.GetExpression(varScope, ((ExpressionStatement)forContext.Iterator).Expression);
                    RawContinue = true;
                }
                // Get the for assignment.
                else
                {
                    Iterator = parseInfo.GetStatement(varScope, forContext.Iterator);
                    RawContinue = false;
                }
            }
            else RawContinue = true;

            // Get the block.
            Block = parseInfo.SetLoop(this).GetStatement(varScope, forContext.Block);
            // Get the path info.
            Path = new PathInfo(Block, forContext.Range, false);
        }

        public override void Translate(ActionSet actionSet)
        {
            if (IsAutoFor)
                TranslateAutoFor(actionSet);
            else
                TranslateFor(actionSet);
        }

        void TranslateFor(ActionSet actionSet)
        {
            if (DefinedVariable != null)
            {
                // Add the defined variable to the index assigner.
                actionSet.IndexAssigner.Add(actionSet.VarCollection, DefinedVariable, actionSet.IsGlobal, null);

                // Set the initial variable.
                if (actionSet.IndexAssigner[DefinedVariable] is IndexReference && DefinedVariable.InitialValue != null)
                    actionSet.AddAction(((IndexReference)actionSet.IndexAssigner[DefinedVariable]).SetVariable(
                        (Element)DefinedVariable.InitialValue.Parse(actionSet)
                    ));
            }
            else if (Initializer != null)
                Initializer.Translate(actionSet);

            // Get the condition.
            Element condition;
            if (Condition != null) condition = (Element)Condition.Parse(actionSet); // User-define condition
            else condition = new V_True(); // No condition, just use true.
            actionSet.AddAction(Element.Part<A_While>(condition));

            Block.Translate(actionSet.Indent());

            // Resolve continues.
            ResolveContinues(actionSet);

            if (Iterator != null)
                Iterator.Translate(actionSet.Indent());

            actionSet.AddAction(new A_End());

            // Resolve breaks.
            ResolveBreaks(actionSet);
        }

        void TranslateAutoFor(ActionSet actionSet)
        {
            WorkshopVariable variable;
            Element target;
            Element start;

            // Existing variable being used in for.
            if (VariableResolve != null)
            {
                VariableElements elements = VariableResolve.ParseElements(actionSet);
                variable = elements.IndexReference.WorkshopVariable;
                target = elements.Target;
                start = (Element)InitialResolveValue?.Parse(actionSet) ?? new V_Number(0);
            }
            // New variable being use in for.
            else
            {
                actionSet.IndexAssigner.Add(actionSet.VarCollection, DefinedVariable, actionSet.IsGlobal, null);
                variable = ((IndexReference)actionSet.IndexAssigner[DefinedVariable]).WorkshopVariable;
                target = new V_EventPlayer();
                start = (Element)DefinedVariable.InitialValue?.Parse(actionSet) ?? new V_Number(0);
            }

            Element stop = (Element)Condition.Parse(actionSet);
            Element step = (Element)Step.Parse(actionSet);

            // Global
            if (variable.IsGlobal)
                actionSet.AddAction(Element.Part<A_ForGlobalVariable>(
                    variable,
                    start, stop, step
                ));
            // Player
            else
                actionSet.AddAction(Element.Part<A_ForPlayerVariable>(
                    target,
                    variable,
                    start, stop, step
                ));

            // Translate the block.
            Block.Translate(actionSet.Indent());

            // Resolve continues.
            ResolveContinues(actionSet);

            // Cap the for.
            actionSet.AddAction(new A_End());

            // Resolve breaks.
            ResolveBreaks(actionSet);
        }
    }

    class ForeachAction : LoopAction
    {
        private Var ForeachVar { get; }
        private IExpression Array { get; }
        private IStatement Block { get; }

        public ForeachAction(ParseInfo parseInfo, Scope scope, Foreach foreachContext)
        {
            RawContinue = false;

            Scope varScope = scope.Child();

            ForeachVar = new ForeachVariable(varScope, new ForeachContextHandler(parseInfo, foreachContext));

            // Get the array that will be iterated on.
            Array = parseInfo.GetExpression(scope, foreachContext.Expression);

            // Get the foreach block.
            Block = parseInfo.SetLoop(this).GetStatement(varScope, foreachContext.Statement);
            // Get the path info.
            Path = new PathInfo(Block, foreachContext.Range, false);
        }

        public override void Translate(ActionSet actionSet)
        {
            ForeachBuilder foreachBuilder = new ForeachBuilder(actionSet, Array.Parse(actionSet), actionSet.IsRecursive);

            // Add the foreach value to the assigner.
            actionSet.IndexAssigner.Add(ForeachVar, foreachBuilder.IndexValue);

            // Translate the block.
            Block.Translate(actionSet.Indent());

            // Resolve continues.
            ResolveContinues(actionSet);

            // Finish the foreach.
            foreachBuilder.Finish();

            // Resolve breaks.
            ResolveBreaks(actionSet);
        }

        class ForeachContextHandler : IVarContextHandler
        {
            public ParseInfo ParseInfo { get; }
            private readonly Foreach _foreachContext;

            public ForeachContextHandler(ParseInfo parseInfo, Foreach foreachContext)
            {
                ParseInfo = parseInfo;
                _foreachContext = foreachContext;
            }

            public VarBuilderAttribute[] GetAttributes() => new VarBuilderAttribute[0];
            public IParseType GetCodeType() => _foreachContext.Type;
            public Location GetDefineLocation() => new Location(ParseInfo.Script.Uri, GetNameRange());
            public string GetName() => _foreachContext.Identifier.Text;

            public DocRange GetNameRange() => _foreachContext.Identifier.Range;
            public DocRange GetTypeRange() => _foreachContext.Type.Range;
            public bool CheckName() => _foreachContext.Identifier;
        }
    }
}