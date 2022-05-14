using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;
using static Sandbox.Definitions.MyOxygenGeneratorDefinition;

namespace BadRefiningMod
{
    class DefinitionModifier
    {
        private delegate void Callable();

        private List<Callable> _undoCommands = new List<Callable>();

        public void Set<T, U>(T definition, Func<T, U> getter, Action<T, U> setter, U value)
        {
            U originalValue = getter(definition);
            setter(definition, value);
            _undoCommands.Add(() => setter(definition, originalValue));
        }

        public void UnsetAll()
        {
            foreach (var command in _undoCommands)
            {
                command();
            }
        }

        public int Count
        {
            get
            {
                return _undoCommands.Count;
            }
        }
    }

    public class Logger
    {
        public static void Log(string msg)
        {
            MyLog.Default.WriteLineAndConsole($"[BadRefining]: {msg}");

            //if (MyAPIGateway.Session?.Player != null)
            //    MyAPIGateway.Utilities.ShowNotification($"[ ERROR: {GetType().FullName}: {e.Message} | Send SpaceEngineers.Log to mod author ]", 10000, MyFontEnum.Red);
        }

        public static void Error(string msg)
        {
            Log("ERROR: " + msg);
        }
    }

    public class ModSettingsUtilities
    {
        public static bool SettingsFileExists<T>(string filename)
        {
            return MyAPIGateway.Utilities.FileExistsInWorldStorage(filename, typeof(T));
        }

        public static T LoadSettingsFile<T>(string filename)
        {
            try
            {
                using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(filename, typeof(T)))
                {
                    string configcontents = reader.ReadToEnd();
                    T config = MyAPIGateway.Utilities.SerializeFromXML<T>(configcontents);
                    Logger.Log("Loaded existing settings from file " + filename);
                    return config;
                }
            }
            catch (Exception e)
            {
                Logger.Error("Failed to load settings from " + filename + ": " + e.Message);
                return default(T);
            }
        }

        public static T LoadOrWriteDefault<T>(T defaultSettings, string filename)
        {
            if (!SettingsFileExists<T>(filename))
            {
                Logger.Log("Configuration file not found: " + filename + ". Using default configuration instead");
                return SaveSettingsFile(defaultSettings, filename);
            }
            else
            {
                return LoadSettingsFile<T>(filename);
            }
        }

        public static T SaveSettingsFile<T>(T settings, string filename)
        {
            try
            {
                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(filename, typeof(T)))
                {
                    writer.Write(MyAPIGateway.Utilities.SerializeToXML<T>(settings));
                }
                Logger.Log("Wrote settings to " + filename);
            }
            catch (Exception e)
            {
                Logger.Error("Failed to write settings to " + filename + ": " + e.Message);
            }

            return settings;
        }
    }

    public class OreConfiguration
    {
        public string OreName = "";

        public OreConfiguration()
        {}

        public OreConfiguration(string oreName)
        {
            OreName = oreName;
        }
    }

    public class BlueprintYieldFactor
    {
        public string BlueprintName;
        public float YieldFactor;

        public BlueprintYieldFactor()
        {}

        public BlueprintYieldFactor(string blueprintName, float yieldFactor)
        {
            BlueprintName = blueprintName;
            YieldFactor = yieldFactor;
        }
    }

    public class ModSettings
    {
        public float ProductionBlockOperationalPowerConsumptionFactor = 1.5f;
        public float ProductionBlockStandbyPowerConsumptionFactor = 1.5f;

        public float OxygenGeneratorIceConsumptionFactor = 2f;
        public float OxygenGeneratorIceToGasRatioFactor = 0.1f;

        public float OxygenFarmMaxGasOutputFactor = 2f;

        public float LargeRefineryIngotYieldFactor = 0.6f;
        public float StoneOreToIngotYieldFactor = 0.5f;
        public float StoneOreToIngotSurvivalKitYieldFactor = 0.35f;

        public BlueprintYieldFactor[] YieldFactorOverrides = new BlueprintYieldFactor[] {
            new BlueprintYieldFactor("UraniumOreToIngot", 0.1f)
        };

        public static ModSettings Load()
        {
            return ModSettingsUtilities.LoadOrWriteDefault(new ModSettings(), "Settings.xml");
        }

        public static ModSettings Save(ModSettings settings)
        {
            return ModSettingsUtilities.SaveSettingsFile(settings, "Settings.xml");
        }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Session : MySessionComponentBase
    {
        private DefinitionModifier _modifier = new DefinitionModifier();
        private ModSettings _settings;

        public static bool IsIngotDefinitionId(MyDefinitionId id)
        {
            return id.TypeId == typeof(MyObjectBuilder_Ingot);
        }

        public static bool BlueprintDefinitionItemsContainsIngot(MyBlueprintDefinition.Item[] items)
        {
            foreach (var result in items)
            {
                if (IsIngotDefinitionId(result.Id))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool BlueprintDefinitionItemsContainsStone(MyBlueprintDefinition.Item[] items)
        {
            foreach (var result in items)
            {
                if (result.Id.SubtypeName == "Stone")
                {
                    return true;
                }
            }
            return false;
        }

        private void PreventBigRefineriesProcessingStone()
        {
            var bigRefineryBlueprintClass = MyDefinitionManager.Static.GetBlueprintClass("RefineryIngots");
            AddLargeRefineryBlueprintsToClass(bigRefineryBlueprintClass);
            SetLargeRefineryBlueprintClass(bigRefineryBlueprintClass);
        }

        private bool IsLargeRefineryIngotsBlueprint(MyBlueprintDefinitionBase blueprint)
        {
            return BlueprintDefinitionItemsContainsIngot(blueprint.Results)
                    && !BlueprintDefinitionItemsContainsStone(blueprint.Results)
                    && !BlueprintDefinitionItemsContainsStone(blueprint.Prerequisites);
        }

        private void AddLargeRefineryBlueprintsToClass(MyBlueprintClassDefinition blueprintClass)
        {
            foreach (var blueprint in MyDefinitionManager.Static.GetBlueprintDefinitions())
            {
                if (IsLargeRefineryIngotsBlueprint(blueprint))
                {
                    Logger.Log("Adding blueprint " + blueprint + " to class " + blueprintClass);
                    blueprintClass.AddBlueprint(blueprint);
                }
            }
        }

        private bool IsSmallRefineryDefinition(MyProductionBlockDefinition definition)
        {
            return definition.CubeSize != MyCubeSize.Large || definition.Id.SubtypeName == "Blast Furnace";
        }

        private void SetLargeRefineryBlueprintClass(MyBlueprintClassDefinition blueprintClass)
        {
            foreach (var definition in MyDefinitionManager.Static.GetAllDefinitions())
            {
                if (!(definition is MyRefineryDefinition))
                {
                    continue;
                }

                var productionBlock = definition as MyProductionBlockDefinition;
                if (IsSmallRefineryDefinition(productionBlock))
                {
                    continue;
                }

                SetProductionBlockBlueprintClass(productionBlock, blueprintClass);
            }
        }

        private void SetProductionBlockBlueprintClass(MyProductionBlockDefinition productionBlock, MyBlueprintClassDefinition blueprintClass)
        {
            Logger.Log("Setting refinery " + productionBlock.Id + " blueprint class to " + blueprintClass);
            var newBlueprintClassesValue = new List<MyBlueprintClassDefinition>();
            newBlueprintClassesValue.Add(blueprintClass);

            _modifier.Set(
                productionBlock,
                def => new List<MyBlueprintClassDefinition>(def.BlueprintClasses),
                (def, v) =>
                {
                    def.BlueprintClasses.Clear();
                    def.BlueprintClasses.AddList(v);
                },
                newBlueprintClassesValue
            );
        }

        private void IncreasePowerConsumptionOfAllProductionBlocks()
        {
            MyDefinitionManager definitions = MyDefinitionManager.Static;
            foreach (var definition in definitions.GetAllDefinitions())
            {
                if (definition is MyProductionBlockDefinition)
                {
                    IncreasePowerConsumptionOfProductionBlock(definition as MyProductionBlockDefinition);
                }
            }
        }

        private void IncreasePowerConsumptionOfProductionBlock(MyProductionBlockDefinition productionBlock)
        {
            var operationalPowerConsumptionFactor = _settings.ProductionBlockOperationalPowerConsumptionFactor;
            var standbyPowerConsumptionFactor = _settings.ProductionBlockStandbyPowerConsumptionFactor;

            Logger.Log("Increasing power consumption of " + productionBlock.Id);

            _modifier.Set(
                productionBlock,
                def => def.OperationalPowerConsumption,
                (def, v) => def.OperationalPowerConsumption = v,
                productionBlock.OperationalPowerConsumption * operationalPowerConsumptionFactor
            );
            _modifier.Set(
                productionBlock,
                def => def.StandbyPowerConsumption,
                (def, v) => def.StandbyPowerConsumption = v,
                productionBlock.StandbyPowerConsumption * standbyPowerConsumptionFactor
            );
        }

        private void MakeAllOxygenGeneratorsBad()
        {
            MyDefinitionManager definitions = MyDefinitionManager.Static;
            foreach (var definition in definitions.GetAllDefinitions())
            {
                if (!(definition is MyOxygenGeneratorDefinition))
                {
                    continue;
                }

                MakeOxygenGeneratorBad(definition as MyOxygenGeneratorDefinition);
            }
        }

        private void MakeOxygenGeneratorBad(MyOxygenGeneratorDefinition definition)
        {
            var iceConsumptionFactor = _settings.OxygenGeneratorIceConsumptionFactor;
            var iceToGasRatioFactor = _settings.OxygenGeneratorIceToGasRatioFactor;

            Logger.Log("Making oxygen generator " + definition.Id + " bad");

            definition.IceConsumptionPerSecond *= 2;
            _modifier.Set(
                definition,
                def => def.IceConsumptionPerSecond,
                (def, v) => def.IceConsumptionPerSecond = v,
                definition.IceConsumptionPerSecond * iceConsumptionFactor
            );

            var newGasProduction = new List<MyGasGeneratorResourceInfo>();
            foreach (var produced in definition.ProducedGases)
            {
                MyGasGeneratorResourceInfo modified = new MyGasGeneratorResourceInfo();
                modified.Id = produced.Id;
                modified.IceToGasRatio = produced.IceToGasRatio * iceToGasRatioFactor;
                newGasProduction.Add(modified);
            }
            _modifier.Set(
                definition,
                def => def.ProducedGases,
                (def, v) => def.ProducedGases = v,
                newGasProduction
            );
        }

        private void IncreaseOxygenFarmOutput()
        {
            var oxygenFarmMaxGasOutputFactor = _settings.OxygenFarmMaxGasOutputFactor;

            Logger.Log("Increasing oxygen farm output");

            MyDefinitionManager definitions = MyDefinitionManager.Static;
            foreach (var definition in MyDefinitionManager.Static.GetAllDefinitions())
            {
                if (!(definition is MyOxygenFarmDefinition))
                {
                    continue;
                }

                var oxygenFarm = definition as MyOxygenFarmDefinition;
                _modifier.Set(
                    oxygenFarm,
                    def => def.MaxGasOutput,
                    (def, v) => def.MaxGasOutput = v,
                    oxygenFarm.MaxGasOutput * oxygenFarmMaxGasOutputFactor
                );
            }
        }

        private void MultiplyBlueprintResultAmounts(MyBlueprintDefinitionBase blueprint, float factor)
        {
            Logger.Log("Multiplying yield of " + blueprint.Id + " by " + factor);

            var newResults = new MyBlueprintDefinitionBase.Item[blueprint.Results.Length];
            for (int i = 0; i < blueprint.Results.Length; ++i)
            {
                newResults[i].Id = blueprint.Results[i].Id;
                newResults[i].Amount = MyFixedPoint.Max(1, blueprint.Results[i].Amount * factor);
            }

            _modifier.Set(
                blueprint,
                def => def.Results,
                (def, v) => def.Results = v,
                newResults
            );
        }

        float GetYieldFactorOverrideOrDefault(string blueprintName, float defaultFactor)
        {
            foreach (var factorConfig in _settings.YieldFactorOverrides)
            {
                if (String.Equals(factorConfig.BlueprintName, blueprintName, StringComparison.OrdinalIgnoreCase))
                {
                    return factorConfig.YieldFactor;
                }
            }
            return defaultFactor;
        }

        private MyBlueprintDefinitionBase GetBlueprintByName(string blueprintName)
        {
            MyBlueprintDefinitionBase found = MyDefinitionManager.Static.GetBlueprintDefinition(
                new MyDefinitionId(
                    typeof(MyObjectBuilder_BlueprintDefinition), 
                    blueprintName
                )
            );
            if (found == null)
            {
                Logger.Error("Blueprint not found: " + blueprintName);
            }
            return found;
        }

        private void ReduceLargeRefineryIngotYields()
        {
            var largeRefineryIngotYieldFactor = _settings.LargeRefineryIngotYieldFactor;

            foreach (var definition in MyDefinitionManager.Static.GetBlueprintDefinitions())
            {
                if (!IsLargeRefineryIngotsBlueprint(definition))
                {
                    continue;
                }

                float factor = GetYieldFactorOverrideOrDefault(definition.Id.SubtypeName, largeRefineryIngotYieldFactor);
                MultiplyBlueprintResultAmounts(definition, factor);
            }
        }

        private void ReduceSmallRefineryIngotYields()
        {
            MultiplyBlueprintResultAmounts(GetBlueprintByName("StoneOreToIngot"), _settings.StoneOreToIngotYieldFactor);
            MultiplyBlueprintResultAmounts(GetBlueprintByName("StoneOreToIngotBasic"), _settings.StoneOreToIngotSurvivalKitYieldFactor);
        }



        public override void LoadData()
        {
            _settings = ModSettings.Load();
            
            Logger.Log("Setting up BadRefining mod...");

            PreventBigRefineriesProcessingStone();
            IncreasePowerConsumptionOfAllProductionBlocks();
            MakeAllOxygenGeneratorsBad();
            IncreaseOxygenFarmOutput();
            ReduceLargeRefineryIngotYields();
            ReduceSmallRefineryIngotYields();

            Logger.Log("Made " + _modifier.Count + " modifications");
        }

        protected override void UnloadData()
        {
            _modifier.UnsetAll();
            Logger.Log("Undid " + _modifier.Count + " modifications");
        }
    }
}
