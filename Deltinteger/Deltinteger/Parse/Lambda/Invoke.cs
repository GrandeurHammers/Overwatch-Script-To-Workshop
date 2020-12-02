using Deltin.Deltinteger.Compiler;
using Deltin.Deltinteger.LanguageServer;
using CompletionItem = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItem;

namespace Deltin.Deltinteger.Parse.Lambda
{
    /// <summary>Lambda invoke function.</summary>
    public class LambdaInvoke : IMethod
    {
        public string Name => "Invoke";
        public CodeType CodeType => LambdaType.ReturnType;
        public CodeParameter[] Parameters { get; }

        public MethodAttributes Attributes => new MethodAttributes();
        public bool Static => false;
        public bool WholeContext => true;
        public string Documentation => "Invokes the lambda expression.";
        public Location DefinedAt => null;
        public AccessLevel AccessLevel => AccessLevel.Public;
        public bool DoesReturnValue => LambdaType.ReturnsValue;
        
        public PortableLambdaType LambdaType { get; }

        public LambdaInvoke(PortableLambdaType lambdaType)
        {
            LambdaType = lambdaType;
            Parameters = ParametersFromTypes(lambdaType.Parameters);
        }

        public IWorkshopTree Parse(ActionSet actionSet, MethodCall methodCall)
        {
            if (LambdaType.IsConstant())
            {
                ILambdaInvocable lambda = (ILambdaInvocable)actionSet.CurrentObject;
                return lambda.Invoke(actionSet, methodCall.ParameterValues);
            }
            return actionSet.DeltinScript.GetComponent<LambdaGroup>().Call(actionSet, methodCall);
        }

        public void Call(ParseInfo parseInfo, DocRange callRange)
        {
            if (LambdaType.LambdaKind != LambdaKind.Anonymous && LambdaType.LambdaKind != LambdaKind.Portable && parseInfo.SourceExpression != null)
                parseInfo.SourceExpression.OnResolve(expr => ConstantExpressionResolver.Resolve(expr, expr => {
                    // Get the lambda that is being invoked.
                    if (expr is ILambdaApplier source)
                    {
                        if (!source.ResolvedSource) return;

                        // Recursion and error check.
                        LambdaInvokeApply(parseInfo, source, callRange);
                        
                        // Parameter invocation states.
                        for (int i = 0; i < source.InvokedState.Length; i++)
                            if (source.InvokedState[i].Invoked)
                                Parameters[i].Invoked.WasInvoked();
                    }
                    // The lambda is being invoked from a parameter.
                    else if (ParameterInvocableBridge(expr, out IBridgeInvocable invocable))
                    {
                        invocable.WasInvoked();
                    }
                    // This will only run if a way to resolve lambdas was not accounted for.
                    // Unresolved lambdas will not throw any errors if a restricted value is inside and the lambda is invoked.
                    // Unresolved lambdas also cannot check for recursion.
                    else parseInfo.Script.Diagnostics.Warning("Source lambda not found, contact zez- I mean, deltin.", callRange);
                }));
        }

        /// <summary>Gets the restricted calls and recursion from a lambda invocation.</summary>
        public static void LambdaInvokeApply(ParseInfo parseInfo, ILambdaApplier source, DocRange callRange)
        {
            if (!source.ResolvedSource) return;

            parseInfo.CurrentCallInfo?.Call(source.RecursiveCallHandler, callRange);

            // Add restricted calls.
            foreach (RestrictedCall call in source.CallInfo.RestrictedCalls)
                parseInfo.RestrictedCallHandler.RestrictedCall(new RestrictedCall(
                    call.CallType,
                    parseInfo.GetLocation(callRange),
                    RestrictedCall.Message_LambdaInvoke(source.GetLabel(false), call.CallType)
                ));
        }

        /// <summary>Determines if an expression resolves to an IBridgeInvocable.</summary>
        /// <param name="expression">The expression to extract the IBridgeInvocable from.</param>
        /// <param name="invocable">The resulting invocable. Will be null if not found.</param>
        /// <returns>True if the invocable is found, false otherwise.</returns>
        public static bool ParameterInvocableBridge(IExpression expression, out IBridgeInvocable invocable)
        {
            if (expression is CallVariableAction callVariable && callVariable.Calling is Var var && var.BridgeInvocable != null)
            {
                invocable = var.BridgeInvocable;
                return true;
            }
            invocable = null;
            return false;
        }

        public CompletionItem GetCompletion() => MethodAttributes.GetFunctionCompletion(this);
        public string GetLabel(bool markdown) => HoverHandler.GetLabel(DoesReturnValue ? CodeType?.Name ?? "define" : "void", Name, Parameters, markdown, Documentation);

        /// <summary>Gets the 'Invoke' parameters from an array of CodeTypes.</summary>
        /// <param name="argumentTypes">The array of CodeTypes. The resulting CodeParameter[] will have an equal length to this.</param>
        private static CodeParameter[] ParametersFromTypes(CodeType[] argumentTypes)
        {
            if (argumentTypes == null) return new CodeParameter[0];

            CodeParameter[] parameters = new CodeParameter[argumentTypes.Length];
            for (int i = 0; i < parameters.Length; i++) parameters[i] = new CodeParameter($"arg{i}", argumentTypes[i]);
            return parameters;
        }
    }
}