﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Deltin.Deltinteger.Parse;
using Deltin.Deltinteger.LanguageServer;
using CompletionItem = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItem;

namespace Deltin.Deltinteger.CustomMethods
{
    [AttributeUsage(AttributeTargets.Class)]
    public class CustomMethod : Attribute
    {
        public CustomMethod(string methodName, string description, CustomMethodType methodType, bool global = true)
        {
            MethodName = methodName;
            Description = description;
            MethodType = methodType;
            Global = global;
        }

        public string MethodName { get; }
        public string Description { get; }
        public CustomMethodType MethodType { get; }
        public bool Global { get; }
    }

    public enum CustomMethodType
    {
        Value,
        MultiAction_Value,
        Action
    }

    public class CustomMethodData : IMethod
    {
        public string Name { get; }
        public CodeParameter[] Parameters { get; }
        public CustomMethodType CustomMethodType { get; }
        public Type Type { get; }
        public bool Global { get; }
        public MethodAttributes Attributes { get; } = new MethodAttributes();

        // IScopeable defaults
        public Location DefinedAt => null;
        public AccessLevel AccessLevel => AccessLevel.Public;
        public bool WholeContext => true;
        public bool Static => true;
        
        public CodeType CodeType => null;

        public string Documentation { get; }

        public CustomMethodData(Type type)
        {
            Type = type;

            CustomMethod data = type.GetCustomAttribute<CustomMethod>();
            Name = data.MethodName;
            CustomMethodType = data.MethodType;
            Documentation = data.Description;
            Global = data.Global;

            var obj = GetObject();
            Parameters = obj.Parameters() ?? new CodeParameter[0];
        }

        private CustomMethodBase GetObject()
        {
            return (CustomMethodBase)Activator.CreateInstance(Type);
        }

        public bool DoesReturnValue => CustomMethodType == CustomMethodType.Value || CustomMethodType == CustomMethodType.MultiAction_Value;

        public IWorkshopTree Parse(ActionSet actionSet, MethodCall methodCall)
        {
            return GetObject().Get(actionSet, methodCall.ParameterValues, methodCall.AdditionalParameterData);
        }

        public string GetLabel(bool markdown) => HoverHandler.GetLabel(CustomMethodType == CustomMethodType.Action ? null : CodeType?.Name ?? "define", Name, Parameters, markdown, Documentation);
        public CompletionItem GetCompletion() => MethodAttributes.GetFunctionCompletion(this);

        static CustomMethodData[] _customMethodData = null;
        public static CustomMethodData[] GetCustomMethods()
        {
            if (_customMethodData == null)
            {
                Type[] types = Assembly.GetExecutingAssembly().GetTypes()
                    .Where(type => type.GetCustomAttribute<CustomMethod>() != null)
                    .ToArray();

                _customMethodData = new CustomMethodData[types.Length];
                for (int i = 0; i < _customMethodData.Length; i++)
                    _customMethodData[i] = new CustomMethodData(types[i]);
            }
            return _customMethodData;
        }
        public static CustomMethodData GetCustomMethod<T>() where T: CustomMethodBase
        {
            foreach (CustomMethodData customMethod in GetCustomMethods())
                if (customMethod.Type == typeof(T))
                    return customMethod;
            return null;
        }
    }

    public abstract class CustomMethodBase
    {
        public abstract CodeParameter[] Parameters();
        public virtual IWorkshopTree Get(ActionSet actionSet, IWorkshopTree[] parameterValues) => throw new NotImplementedException();
        public virtual IWorkshopTree Get(ActionSet actionSet, IWorkshopTree[] parameterValues, object[] additionalParameterData)
        {
            return Get(actionSet, parameterValues);
        }
    }
}
