﻿using System.Collections.Generic;
using System.Linq;
using Libplanet.Action;
using Nekoyume.Model;
using Nekoyume.Model.BattleStatus.Arena;
using Nekoyume.Model.Skill;
using Nekoyume.TableData;
using Priority_Queue;

namespace Nekoyume.Arena
{
    /// <summary>
    /// Changed at https://github.com/planetarium/lib9c/pull/2229
    /// </summary>
    public class ArenaSimulator : IArenaSimulator
    {
        private const decimal TurnPriority = 100m;
        private const int MaxTurn = 200;

        public IRandom Random { get; }
        public int Turn { get; private set; }
        public ArenaLog Log { get; private set; }
        public int HpModifier { get; }

        public ArenaSimulator(IRandom random, int hpModifier = 2)
        {
            Random = random;
            Turn = 1;
            HpModifier = hpModifier;
        }

        public ArenaLog Simulate(
            ArenaPlayerDigest challenger,
            ArenaPlayerDigest enemy,
            ArenaSimulatorSheets sheets,
            bool setExtraValueBuffBeforeGetBuffs = false)
        {
            Log = new ArenaLog();
            var players = SpawnPlayers(this, challenger, enemy, sheets, Log, setExtraValueBuffBeforeGetBuffs);
            Turn = 1;

            while (true)
            {
                if (Turn > MaxTurn)
                {
                    // todo : 턴오버일경우 정책 필요함 일단 Lose
                    Log.Result = ArenaLog.ArenaResult.Lose;
                    break;
                }

                if (!players.TryDequeue(out var selectedPlayer))
                {
                    break;
                }

                selectedPlayer.Tick();

                var deadPlayers = players.Where(x => x.IsDead);
                var arenaCharacters = deadPlayers as ArenaCharacter[] ?? deadPlayers.ToArray();
                if (arenaCharacters.Any())
                {
                    var (deadPlayer, result) = GetBattleResult(arenaCharacters);
                    Log.Result = result;
                    Log.Add(new ArenaDead((ArenaCharacter)deadPlayer.Clone()));
                    Log.Add(new ArenaTurnEnd((ArenaCharacter)selectedPlayer.Clone(), Turn));
                    break;
                }

                if (!selectedPlayer.IsEnemy)
                {
                    Log.Add(new ArenaTurnEnd((ArenaCharacter)selectedPlayer.Clone(), Turn));
                    Turn++;
                }

                foreach (var other in players)
                {
                    var spdMultiplier = 0.6m;
                    var current = players.GetPriority(other);
                    var skills = sheets.SkillSheet.OrderedList.Select(s => s.SkillCategory);
                    var skillCategory =
                        other.usedSkill?.SkillInfos.Where(si => skills.Contains(si.SkillCategory))
                            .Select(si => si.SkillCategory);
                    if (skillCategory != null && skillCategory.Any(sc => sc != SkillCategory.NormalAttack))
                    {
                        spdMultiplier = 0.9m;
                    }

                    var speed = current * spdMultiplier;
                    players.UpdatePriority(other, speed);
                }

                players.Enqueue(selectedPlayer, TurnPriority / selectedPlayer.SPD);
            }

            return Log;
        }

        private static (ArenaCharacter, ArenaLog.ArenaResult) GetBattleResult(
            IReadOnlyCollection<ArenaCharacter> deadPlayers)
        {
            if (deadPlayers.Count > 1)
            {
                var enemy = deadPlayers.First(x => x.IsEnemy);
                return (enemy, ArenaLog.ArenaResult.Win);
            }

            var player = deadPlayers.First();
            return (player, player.IsEnemy ? ArenaLog.ArenaResult.Win : ArenaLog.ArenaResult.Lose);
        }

        private static SimplePriorityQueue<ArenaCharacter, decimal> SpawnPlayers(
            ArenaSimulator simulator,
            ArenaPlayerDigest challengerDigest,
            ArenaPlayerDigest enemyDigest,
            ArenaSimulatorSheets simulatorSheets,
            ArenaLog log,
            bool setExtraValueBuffBeforeGetBuffs = false)
        {
            var challenger = new ArenaCharacter(
                simulator,
                challengerDigest,
                simulatorSheets,
                simulator.HpModifier,
                setExtraValueBuffBeforeGetBuffs: setExtraValueBuffBeforeGetBuffs);
            if (challengerDigest.Runes != null)
            {
                challenger.SetRune(
                    challengerDigest.Runes,
                    simulatorSheets.RuneOptionSheet,
                    simulatorSheets.SkillSheet);
            }

            var enemy = new ArenaCharacter(
                simulator,
                enemyDigest,
                simulatorSheets,
                simulator.HpModifier,
                isEnemy: true,
                setExtraValueBuffBeforeGetBuffs: setExtraValueBuffBeforeGetBuffs);
            if (enemyDigest.Runes != null)
            {
                enemy.SetRune(
                    enemyDigest.Runes,
                    simulatorSheets.RuneOptionSheet,
                    simulatorSheets.SkillSheet);
            }

            challenger.Spawn(enemy);
            enemy.Spawn(challenger);

            log.Add(new ArenaSpawnCharacter((ArenaCharacter)challenger.Clone()));
            log.Add(new ArenaSpawnCharacter((ArenaCharacter)enemy.Clone()));

            var players = new SimplePriorityQueue<ArenaCharacter, decimal>();
            players.Enqueue(challenger, TurnPriority / challenger.SPD);
            players.Enqueue(enemy, TurnPriority / enemy.SPD);
            return players;
        }
    }
}
