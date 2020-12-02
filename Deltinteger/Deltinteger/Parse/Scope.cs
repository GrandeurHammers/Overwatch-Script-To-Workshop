using System;
using System.Collections.Generic;
using System.Linq;
using Deltin.Deltinteger.LanguageServer;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.Compiler;
using CompletionItem = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItem;
using CompletionItemKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItemKind;

namespace Deltin.Deltinteger.Parse
{
    public class Scope
    {
        private readonly List<IVariable> _variables = new List<IVariable>();
        private readonly List<IMethod> _functions = new List<IMethod>();
        private readonly List<MethodGroup> _methodGroups = new List<MethodGroup>();
        public Scope Parent { get; }
        public string ErrorName { get; set; } = "current scope";
        public CodeType This { get; set; }
        public bool PrivateCatch { get; set; }
        public bool ProtectedCatch { get; set; }
        public bool CompletionCatch { get; set; }
        public bool MethodContainer { get; set; }
        public bool CatchConflict { get; set; }

        public Scope() {}
        private Scope(Scope parent)
        {
            Parent = parent;
        }
        public Scope(string name)
        {
            ErrorName = name;
        }
        private Scope(Scope parent, string name)
        {
            Parent = parent;
            ErrorName = name;
        }

        public Scope Child()
        {
            return new Scope(this);
        }

        public Scope Child(string name)
        {
            return new Scope(this, name);
        }

        private void IterateElements(bool iterateVariables, bool iterateMethods, Func<ScopeIterate, ScopeIterateAction> element, Func<Scope, ScopeIterateAction> onEmpty = null)
        {
            Scope current = this;

            bool getPrivate = true;
            bool getProtected = true;

            while (current != null)
            {
                List<IScopeable> checkScopeables = new List<IScopeable>();

                // If variables are being iterated, add them to the list.
                if (iterateVariables) checkScopeables.AddRange(current._variables);

                // If functions are being iterated, add them to the list.
                if (iterateMethods)
                    foreach (var group in current._methodGroups)
                        checkScopeables.AddRange(group.Functions);

                bool stopAfterScope = false;

                foreach (IScopeable check in checkScopeables)
                {
                    // Check if the accessor is valid.
                    bool accessorMatches = check.AccessLevel == AccessLevel.Public ||
                        (getPrivate   && check.AccessLevel == AccessLevel.Private) ||
                        (getProtected && check.AccessLevel == AccessLevel.Protected);

                    ScopeIterateAction action = element(new ScopeIterate(current, check, accessorMatches));
                    if (action == ScopeIterateAction.Stop) return;
                    if (action == ScopeIterateAction.StopAfterScope) stopAfterScope = true;
                }
                // If there are no scopeables and onEmpty is not null, invoke onEmpty. 
                if (checkScopeables.Count == 0 && onEmpty != null)
                {
                    ScopeIterateAction action = onEmpty.Invoke(current);
                    if (action != ScopeIterateAction.Continue) return;
                }

                if (current.PrivateCatch) getPrivate = false;
                if (current.ProtectedCatch) getProtected = false;
                if (stopAfterScope) return;

                current = current.Parent;
            }
        }

        private void IterateParents(Func<Scope, bool> iterate)
        {
            Scope current = this;
            while (current != null)
            {
                if (iterate(current)) return;
                current = current.Parent;
            }
        }

        public void CopyAll(Scope other, Scope getter)
        {
            other.IterateParents(scope => {
                _methodGroups.AddRange(scope._methodGroups);
                return true;
            });

            other.IterateElements(true, true, iterate => {
                // Add the element.
                if (iterate.Element is IVariable variable) _variables.Add(variable);

                if (iterate.Container.PrivateCatch || iterate.Container.CompletionCatch) return ScopeIterateAction.StopAfterScope;
                return ScopeIterateAction.Continue;
            }, scope => {
                // On empty scope.
                if (scope.PrivateCatch || scope.CompletionCatch) return ScopeIterateAction.StopAfterScope;
                return ScopeIterateAction.Continue;
            });
        }

        /// <summary>
        /// Adds a variable to the current scope.
        /// When handling variables added by the user, supply the diagnostics and range to show the syntax error at.
        /// When handling variables added internally, have the diagnostics and range parameters be null. An exception will be thrown instead if there is a syntax error.
        /// </summary>
        /// <param name="variable">The variable that will be added to the current scope. If the object reference is already in the direct scope, an exception will be thrown.</param>
        /// <param name="diagnostics">The file diagnostics to throw errors with. Should be null when adding variables internally.</param>
        /// <param name="range">The document range to throw errors at. Should be null when adding variables internally.</param>
        public void AddVariable(IVariable variable, FileDiagnostics diagnostics, DocRange range)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));
            if (_variables.Contains(variable)) throw new Exception("variable reference is already in scope.");

            if (Conflicts(variable))
                diagnostics.Error(string.Format("A variable of the name {0} was already defined in this scope.", variable.Name), range);
            else
                _variables.Add(variable);
        }

        public void AddNativeVariable(IVariable variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));
            if (_variables.Contains(variable)) throw new Exception("variable reference is already in scope.");
            _variables.Add(variable);
        }

        /// <summary>Adds a variable to the scope that already belongs to another scope.</summary>
        public void CopyVariable(IVariable variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));
            if (!_variables.Contains(variable))
                _variables.Add(variable);
        }

        public bool IsVariable(string name) => GetVariable(name, false) != null;

        public IVariable GetVariable(string name, bool methodGroupsOnly)
        {
            IVariable element = null;
            Scope current = this;

            while (current != null && element == null)
            {
                element = current._variables.Where(v => !methodGroupsOnly || v is MethodGroup || v.CodeType is Lambda.PortableLambdaType).FirstOrDefault(element => element.Name == name);
                current = current.Parent;
            }

            return element;
        }

        public IVariable GetVariable(string name, Scope getter, FileDiagnostics diagnostics, DocRange range, bool methodGroupsOnly)
        {
            IVariable element = GetVariable(name, methodGroupsOnly);

            if (range != null && element == null)
                diagnostics.Error(string.Format("The variable {0} does not exist in the {1}.", name, ErrorName), range);
            
            if (element != null && getter != null && !getter.AccessorMatches(element))
            {
                if (range == null) throw new Exception();
                diagnostics.Error(string.Format("'{0}' is inaccessable due to its access level.", name), range);
            }

            return element;
        }

        public bool Conflicts(IScopeable scopeable, bool variables = true, bool functions = true)
        {
            bool conflicts = false;
            IterateElements(variables, functions, action => {
                // If the element name matches, set conflicts to true then stop iterating.
                if (scopeable.Name == action.Element.Name)
                {
                    if (functions || action.Element is MethodGroup == false)
                        conflicts = true;
                    return ScopeIterateAction.Stop;
                }
                
                return action.Container.CatchConflict ? ScopeIterateAction.StopAfterScope : ScopeIterateAction.Continue;
            }, scope => {
                return scope.CatchConflict ? ScopeIterateAction.StopAfterScope : ScopeIterateAction.Continue;
            });
            return conflicts;
        }

        /// <summary>
        /// Adds a method to the current scope.
        /// When handling methods added by the user, supply the diagnostics and range to show the syntax error at.
        /// When handling methods added internally, have the diagnostics and range parameters be null. An exception will be thrown instead if there is a syntax error.
        /// </summary>
        /// <param name="method">The method that will be added to the current scope. If the object reference is already in the direct scope, an exception will be thrown.</param>
        /// <param name="diagnostics">The file diagnostics to throw errors with. Should be null when adding methods internally.</param>
        /// <param name="range">The document range to throw errors at. Should be null when adding methods internally.</param>
        public void AddMethod(IMethod method, FileDiagnostics diagnostics, DocRange range, bool checkConflicts = true)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            if (checkConflicts && HasConflict(method))
            {
                string message = "A method with the same name and parameter types was already defined in this scope.";

                if (diagnostics != null && range != null)
                {
                    diagnostics.Error(message, range);
                    return;
                }
                else
                    throw new Exception(message);
            }

            AddNativeMethod(method);
        }

        public void AddMacro(MacroVar macro, FileDiagnostics diagnostics, DocRange range, bool checkConflicts = true)
        {
            if (macro == null) throw new ArgumentNullException(nameof(macro));
            if (_variables.Contains(macro)) throw new Exception("macro reference is already in scope.");

            if (checkConflicts && HasConflict(macro))
            {
                string message = "A macro with the same name and parameter types was already defined in this scope.";

                if (diagnostics != null && range != null)
                {
                    diagnostics.Error(message, range);
                    return;
                }
                else
                    throw new Exception(message);
            }

            _variables.Add(macro);
        }

        public void AddNativeMethod(IMethod method)
        {
            foreach (var group in _methodGroups)
                if (group.MethodIsValid(method))
                {
                    group.AddMethod(method);
                    return;
                }
            
            var newGroup = new MethodGroup(method.Name);
            newGroup.AddMethod(method);
            _variables.Add(newGroup);
            _methodGroups.Add(newGroup);
        }

        /// <summary>
        /// Blindly copies a method to the current scope without doing any syntax checking.
        /// Use this to link to a method that already belongs to another scope. The other scope should have already handled the syntax checking.
        /// </summary>
        /// <param name="method">The method to copy.</param>
        public void CopyMethod(IMethod method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            AddNativeMethod(method);
        }

        /// <summary>Checks if a method conflicts with another method in the scope.</summary>
        /// <param name="method">The method to check.</param>
        /// <returns>Returns true if the current scope already has the same name and parameters as the input method.</returns>
        public bool HasConflict(IMethod method) => Conflicts(method, functions: false) || GetMethodOverload(method) != null;

        public bool HasConflict(MacroVar macro) => GetMacroOverload(macro.Name, macro.DefinedAt) != null;

        /// <summary>Gets a method in the scope that has the same name and parameter types. Can potentially resolve to itself if the method being tested is in the scope.</summary>
        /// <param name="method">The method to get a matching overload.</param>
        /// <returns>A method with the matching overload, or null if none is found.</returns>
        public IMethod GetMethodOverload(IMethod method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return GetMethodOverload(method.Name, method.Parameters.Select(p => p.Type).ToArray());
        }

        /// <summary>Gets a method overload in the scope that has the same name and parameter types.</summary>
        /// <param name="name">The name of the method.</param>
        /// <param name="parameterTypes">The types of the parameters.</param>
        /// <returns>A method with the name and parameter types, or null if none is found.</returns>
        public IMethod GetMethodOverload(string name, CodeType[] parameterTypes)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (parameterTypes == null) throw new ArgumentNullException(nameof(parameterTypes));

            IMethod method = null;

            IterateElements(false, true, itElement => {
                // Convert the current element to an IMethod for checking.
                IMethod checking = (IMethod)itElement.Element;

                // If the name does not match or the number of parameters are not equal, continue.
                if (checking.Name != name || checking.Parameters.Length != parameterTypes.Length) return ScopeIterateAction.Continue;

                // Loop through all parameters.
                for (int p = 0; p < checking.Parameters.Length; p++)
                    // If the parameter types do not match, continue.
                    if (checking.Parameters[p].Type != parameterTypes[p])
                        return ScopeIterateAction.Continue;
                
                // Parameter overload matches.
                method = checking;
                return ScopeIterateAction.Stop;
            });

            return method;
        }

        public IVariable GetMacroOverload(string name, Location definedAt)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            IVariable variable = null;

            IterateElements(true, false, itElement => {
                // Convert the current element to an IMethod for checking.
                IVariable checking = (IVariable)itElement.Element;

                // If the name does not match or the number of parameters are not equal, continue.
                if (checking.Name != name || checking.DefinedAt == definedAt) return ScopeIterateAction.Continue;

                // Loop through all parameters.
               
                // Parameter overload matches.
                variable = checking;
                return ScopeIterateAction.Stop;
            });

            return variable;

        }

        /// <summary>Gets all methods in the scope with the provided name.</summary>
        /// <param name="name">The name of the methods.</param>
        /// <returns>An array of methods with the matching name.</returns>
        public IMethod[] GetMethodsByName(string name)
        {
            List<IMethod> methods = new List<IMethod>();

            IterateParents(scope => {
                if (scope.TryGetGroupByName(name, out var group))
                    methods.AddRange(group.Functions);
                return false;
            });

            return methods.ToArray();
        }

        public CodeType GetThis()
        {
            CodeType @this = null;
            Scope current = this;

            while (@this == null && current != null)
            {
                @this = current.This;
                current = current.Parent;
            }

            return @this;
        }

        public bool AccessorMatches(IScopeable element)
        {
            if (element.AccessLevel == AccessLevel.Public) return true;

            bool matches = false;

            IterateElements(true, true, itElement => {
                if (element == itElement.Element)
                {
                    matches = true;
                    return ScopeIterateAction.Stop;
                }

                if ((itElement.Container.PrivateCatch && element.AccessLevel == AccessLevel.Private) ||
                    (itElement.Container.ProtectedCatch && element.AccessLevel == AccessLevel.Protected))
                    return ScopeIterateAction.StopAfterScope;
                return ScopeIterateAction.Continue;
            });

            return matches;
        }

        public bool AccessorMatches(Scope lookingForScope, AccessLevel accessLevel)
        {
            // Just return true if the access level is public.
            if (accessLevel == AccessLevel.Public) return true;

            Scope current = this;
            while (current != null)
            {
                // If the current scope is the scope being looked for, return true.
                if (current == lookingForScope)
                    return true;

                // If the current scope catches private elements and the target access level is private, return false.
                if (current.PrivateCatch && accessLevel == AccessLevel.Private) return false;

                // If the current scope catches protected elements and the target access level is protected, return false.
                if (current.ProtectedCatch && accessLevel == AccessLevel.Protected) return false;

                // Next current is parent.
                current = current.Parent;
            }

            return false;
        }

        public CompletionItem[] GetCompletion(DocPos pos, bool immediate, Scope getter = null)
        {
            var completions = new List<CompletionItem>(); // The list of completion items in this scope.

            // Get the functions.
            var batches = new List<FunctionBatch>();
            IterateParents(scope => {
                // Iterate through each group.
                foreach (var group in scope._methodGroups)
                // Iterate through each function in the group.
                foreach (var func in group.Functions)
                // If the function is scoped at pos,
                // add it to a batch.
                if (scope.WasScopedAtPosition(func, pos, getter))
                {
                    bool batchFound = false; // Determines if a batch was found for the function.

                    // Iterate through each existing batch.
                    foreach (var batch in batches)
                    // If the current batch's name is equal to the function's name, add it to the batch.
                    if (batch.Name == func.Name)
                    {
                        batch.Add();
                        batchFound = true;
                        break;
                    }

                    // If no batch was found for the function name, create a new batch.
                    if (!batchFound)
                        batches.Add(new FunctionBatch(func.Name, func));
                }

                // Add the variables.
                foreach (var variable in scope._variables)
                    if (variable is MethodGroup == false && scope.WasScopedAtPosition(variable, pos, getter))
                        completions.Add(variable.GetCompletion());

                return scope.CompletionCatch;
            });

            // Get the batch completion.
            foreach (var batch in batches)
                completions.Add(batch.GetCompletion());
                
            return completions.ToArray();
        }

        private bool WasScopedAtPosition(IScopeable element, DocPos pos, Scope getter)
        {
            return (pos == null || element.DefinedAt == null || element.WholeContext || element.DefinedAt.range.Start <= pos) && (getter == null || getter.AccessorMatches(element));
        }

        private bool TryGetGroupByName(string name, out MethodGroup group)
        {
            foreach (var g in _methodGroups)
                if (g.Name == name)
                {
                    group = g;
                    return true;
                }
            group = null;
            return false;
        }

        public static Scope GetGlobalScope()
        {
            Scope globalScope = new Scope();

            // Add workshop methods
            foreach (var workshopMethod in ElementList.Elements)
                if (!workshopMethod.Hidden)
                    globalScope.AddNativeMethod(workshopMethod);
            
            // Add custom methods
            foreach (var builtInMethod in CustomMethods.CustomMethodData.GetCustomMethods())
                if (builtInMethod.Global)
                    globalScope.AddNativeMethod(builtInMethod);
            
            globalScope.AddNativeMethod(new Lambda.WaitAsyncFunction());
            globalScope.AddNativeMethod(Animation.AnimationTestingFunctions.Rotate);
            globalScope.AddNativeMethod(Animation.AnimationTestingFunctions.Rotate2);
            return globalScope;
        }

        public bool ScopeContains(IScopeable element)
        {
            // Variable
            if (element is IVariable variable) return ScopeContains(variable);
            // Function
            else if (element is IMethod function) return ScopeContains(function);
            else throw new NotImplementedException();
        }

        public bool ScopeContains(IVariable variable)
        {
            bool found = false;
            IterateElements(true, true, iterate => {
                if (iterate.Element == variable)
                {
                    found = true;
                    return ScopeIterateAction.Stop;
                }
                return ScopeIterateAction.Continue;
            });
            return found;
        }

        public bool ScopeContains(IMethod function)
        {
            bool found = false;
            IterateParents(scope => {
                found = scope.TryGetGroupByName(function.Name, out var group) && group.Functions.Contains(function);
                return found;
            });
            return found;
        }
    
        public void EndScope(ActionSet actionSet, bool includeParents)
        {
            if (MethodContainer) return;

            foreach (IScopeable variable in _variables)
                if (variable is IIndexReferencer referencer && // If the current scopeable is an IIndexReferencer,
                    actionSet.IndexAssigner.TryGet(referencer, out IGettable gettable) && // and the current scopeable is assigned to an index,
                    gettable is RecursiveIndexReference recursiveIndexReference) // and the assigned index is a RecursiveIndexReference,
                    // Pop the variable stack.
                    actionSet.AddAction(recursiveIndexReference.Pop());
            
            if (includeParents && Parent != null)
                Parent.EndScope(actionSet, true);
        }
    }

    class ScopeIterate
    {
        public Scope Container { get; }
        public IScopeable Element { get; }
        public bool AccessorMatches { get; }

        public ScopeIterate(Scope container, IScopeable element, bool accessorMatches)
        {
            Container = container;
            Element = element;
            AccessorMatches = accessorMatches;
        }
    }

    enum ScopeIterateAction
    {
        Continue,
        Stop,
        StopAfterScope
    }

    class FunctionBatch
    {
        public string Name { get; }
        public IMethod Primary { get; }
        public int Overloads { get; private set; }

        public FunctionBatch(string name, IMethod primary)
        {
            Name = name;
            Primary = primary;
        }
        
        public void Add() => Overloads++;

        public CompletionItem GetCompletion() => new CompletionItem() {
            Label = Name,
            Kind = CompletionItemKind.Function,
            Documentation = Primary.Documentation,
            Detail = IMethod.GetLabel(Primary, true)
            // Fancy label (similiar to what c# does)
            // Documentation = new MarkupBuilder()
            //     .StartCodeLine()
            //     .Add(
            //         (Primary.DoesReturnValue ? (Primary.ReturnType == null ? "define" : Primary.ReturnType.GetName()) : "void") + " " +
            //         Primary.GetLabel(false) + (Overloads == 0 ? "" : " (+" + Overloads + " overloads)")
            //     ).EndCodeLine().ToMarkup()
        };
    }
}