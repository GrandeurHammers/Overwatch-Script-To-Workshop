using System;
using System.Collections.Generic;
using System.Linq;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.LanguageServer;
using Deltin.Deltinteger.Parse;

namespace Deltin.Deltinteger.CustomMethods
{
    [CustomMethod("CompareMap", "Compares the current map to a map value. Map variants are considered as well.", CustomMethodType.Value)]
    class CompareCurrentMap : CustomMethodBase
    {
        public override CodeParameter[] Parameters() => new CodeParameter[] {
            new MapParameter()
        };

        public override IWorkshopTree Get(ActionSet actionSet, IWorkshopTree[] parameterValues)
        {
            var enumData = (EnumMember)parameterValues[0];
            Map map = (Map)enumData.Value;
            MapLink mapLink = MapLink.GetMapLink(map);

            if (mapLink == null)
                return new V_Compare(new V_CurrentMap(), Operators.Equal, Element.Part<V_MapVar>(enumData));
            else
                return Element.Part<V_ArrayContains>(mapLink.GetArray(), new V_CurrentMap());
        }
    }

    class MapParameter : CodeParameter
    {
        public MapParameter() : base("map", "The map to compare.") {}

        public override IWorkshopTree Parse(ActionSet actionSet, IExpression expression, object additionalParameterData)
        {
            return base.Parse(actionSet, expression, additionalParameterData);
        }

        public override object Validate(ParseInfo parseInfo, IExpression value, DocRange valueRange)
        {
            var variableCallAction = ExpressionTree.ResultingExpression(value) as EnumValuePair;

            if (variableCallAction == null || variableCallAction.Member.Value is Map == false)
            {
                parseInfo.Script.Diagnostics.Error("Expected a map value.", valueRange);
                return null;
            }

            return (Map)variableCallAction.Member.Value;
        }
    }

    class MapLink
    {
        public Map[] Maps { get; }

        public MapLink(params Map[] maps)
        {
            Maps = maps;
        }

        public Element GetArray()
        {
            return Element.CreateArray(
                // Convert the maps to EnumMembers encased in V_MapVar.
                Maps.Select(m => Element.Part<V_MapVar>(EnumData.GetEnumValue(m)))
                .ToArray()
            );
        }

        public static MapLink GetMapLink(Map map)
        {
            return MapLinks.FirstOrDefault(m => m.Maps.Contains(map));
        }

        public static readonly MapLink[] MapLinks = new MapLink[] {
            new MapLink(Map.Black_Forest, Map.Black_Forest_Winter),
            new MapLink(Map.Blizzard_World, Map.Blizzard_World_Winter),
            new MapLink(Map.Busan, Map.Busan_Downtown_Lunar, Map.Busan_Sanctuary_Lunar, Map.Busan_Stadium),
            new MapLink(Map.Chateau_Guillard, Map.Chateau_Guillard_Halloween),
            new MapLink(Map.Ecopoint_Antarctica, Map.Ecopoint_Antarctica_Winter),
            new MapLink(Map.Eichenwalde, Map.Eichenwalde_Halloween),
            new MapLink(Map.Hanamura, Map.Hanamura_Winter),
            new MapLink(Map.Hollywood, Map.Hollywood_Halloween),
            new MapLink(Map.Ilios, Map.Ilios_Lighthouse, Map.Ilios_Ruins, Map.Ilios_Well),
            new MapLink(Map.Lijiang_Control_Center, Map.Lijiang_Control_Center_Lunar, Map.Lijiang_Garden, Map.Lijiang_Garden_Lunar, Map.Lijiang_Night_Market, Map.Lijiang_Night_Market_Lunar, Map.Lijiang_Tower, Map.Lijiang_Tower_Lunar),
            new MapLink(Map.Nepal, Map.Nepal_Sanctum, Map.Nepal_Shrine, Map.Nepal_Village),
            new MapLink(Map.Oasis, Map.Oasis_City_Center, Map.Oasis_Gardens, Map.Oasis_University)
        };
    }
}