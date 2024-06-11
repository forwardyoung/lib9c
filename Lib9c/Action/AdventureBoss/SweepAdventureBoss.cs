using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bencodex.Types;
using Libplanet.Action;
using Libplanet.Action.State;
using Libplanet.Crypto;
using Nekoyume.Action.Exceptions.AdventureBoss;
using Nekoyume.Battle;
using Nekoyume.Battle.AdventureBoss;
using Nekoyume.Data;
using Nekoyume.Extensions;
using Nekoyume.Helper;
using Nekoyume.Model.AdventureBoss;
using Nekoyume.Model.Arena;
using Nekoyume.Model.Item;
using Nekoyume.Model.State;
using Nekoyume.Module;
using Nekoyume.TableData;
using Nekoyume.TableData.AdventureBoss;
using Nekoyume.TableData.Rune;

namespace Nekoyume.Action.AdventureBoss
{
    [Serializable]
    [ActionType(TypeIdentifier)]
    public class SweepAdventureBoss : GameAction
    {
        public const string TypeIdentifier = "sweep_adventure_boss";

        public const int UnitApPotion = 2;

        public int Season;
        public Address AvatarAddress;

        protected override IImmutableDictionary<string, IValue> PlainValueInternal =>
            new Dictionary<string, IValue>
            {
                ["season"] = (Integer)Season,
                ["avatarAddress"] = AvatarAddress.Serialize(),
            }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(
            IImmutableDictionary<string, IValue> plainValue)
        {
            Season = (Integer)plainValue["season"];
            AvatarAddress = plainValue["avatarAddress"].ToAddress();
        }

        public override IWorld Execute(IActionContext context)
        {
            context.UseGas(1);
            var states = context.PreviousState;

            // Validation
            var latestSeason = states.GetLatestAdventureBossSeason();
            if (latestSeason.Season != Season)
            {
                throw new InvalidAdventureBossSeasonException(
                    $"Given season {Season} is not current season: {latestSeason.Season}"
                );
            }

            if (context.BlockIndex > latestSeason.EndBlockIndex)
            {
                throw new InvalidSeasonException(
                    $"Season finished at block {latestSeason.EndBlockIndex}."
                );
            }

            var avatarState = states.GetAvatarState(AvatarAddress);
            if (avatarState.agentAddress != context.Signer)
            {
                throw new InvalidAddressException();
            }

            var exploreBoard = states.GetExploreBoard(Season);
            var explorer = states.TryGetExplorer(Season, AvatarAddress, out var exp)
                ? exp
                : new Explorer(AvatarAddress, avatarState.name);

            if (explorer.Floor == 0)
            {
                throw new InvalidOperationException("Cannot sweep without cleared stage.");
            }

            // Use AP Potions
            var requiredPotion = explorer.Floor * UnitApPotion;
            var sheets = states.GetSheets(
                containSimulatorSheets: true,
                sheetTypes: new[]
                {
                    typeof(MaterialItemSheet),
                    typeof(RuneListSheet),
                    typeof(RuneLevelBonusSheet),
                    typeof(AdventureBossFloorWaveSheet),
                });
            var materialSheet = sheets.GetSheet<MaterialItemSheet>();
            var material =
                materialSheet.OrderedList.First(row => row.ItemSubType == ItemSubType.ApStone);
            var inventory = states.GetInventoryV2(AvatarAddress);
            if (!inventory.RemoveFungibleItem(material.ItemId, context.BlockIndex,
                    requiredPotion))
            {
                throw new NotEnoughMaterialException(
                    $"{requiredPotion} AP potions needed. You only have {inventory.Items.First(item => item.item.ItemSubType == ItemSubType.ApStone).count}"
                );
            }

            exploreBoard.AddExplorer(AvatarAddress, avatarState.name);
            exploreBoard.UsedApPotion += requiredPotion;
            explorer.UsedApPotion += requiredPotion;

            var simulator = new AdventureBossSimulator(
                latestSeason.BossId, explorer.Floor, context.GetRandom(),
                avatarState, sheets.GetSimulatorSheets(), logEvent: false
            );
            simulator.AddBreakthrough(1, explorer.Floor, sheets.GetSheet<AdventureBossFloorWaveSheet>());

            // Add point, reward
            var point = 0;
            var rewardList = new List<AdventureBossGameData.ExploreReward>();
            var random = context.GetRandom();
            var selector = new WeightedSelector<AdventureBossGameData.ExploreReward>(random);
            for (var fl = 1; fl <= explorer.Floor; fl++)
            {
                var (min, max) = AdventureBossGameData.PointDict[fl];
                point += random.Next(min, max + 1);

                selector.Clear();
                var floorReward = AdventureBossGameData.AdventureBossRewards
                    .First(rw => rw.BossId == latestSeason.BossId).exploreReward[fl];
                foreach (var reward in floorReward.Reward)
                {
                    selector.Add(reward, reward.Ratio);
                }

                rewardList.Add(selector.Select(1).First());
            }

            exploreBoard.TotalPoint += point;
            explorer.Score += point;
            states = AdventureBossHelper.AddExploreRewards(context, states, AvatarAddress,
                inventory, rewardList);

            return states
                .SetInventory(AvatarAddress, inventory)
                .SetExploreBoard(Season, exploreBoard)
                .SetExplorer(Season, explorer);
        }
    }
}
