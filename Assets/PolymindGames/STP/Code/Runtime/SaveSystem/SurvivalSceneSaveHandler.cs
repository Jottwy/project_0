using PolymindGames.WorldManagement;
using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    [SelectionBase]
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    public sealed class SurvivalSceneSaveHandler : SceneSaveHandler
    {
        public const string DifficultyKey = "Difficulty";
        public const string GameTimeKey = "GameTime";

        public override Dictionary<string, object> CollectSceneInfoData()
        {
            return new Dictionary<string, object>
            {
                { DifficultyKey, GameDifficulty.Standard },
                { GameTimeKey, World.Instance.Time.GetGameTime() }
            };
        }
    }
}