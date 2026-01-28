using System.Collections.Generic;
using UnityEngine;

public static class CyclePicker
    {
        public static int NextFromCycleFiltered(
            List<int> cycle,
            int length,
            int lastUsed,
            HashSet<int> blacklist,
            out int newLastUsed)
        {
            newLastUsed = lastUsed;
            if (length <= 0) return -1;

            if (cycle == null) cycle = new List<int>(length);

            if (cycle.Count == 0)
            {
                // Refill
                ControllerMain.LogInfo($"[CyclePicker] Refilling cycle (PoolSize={length})...");
                cycle.Clear();
                for (int i = 0; i < length; i++)
                {
                    if (blacklist == null || !blacklist.Contains(i)) 
                        cycle.Add(i);
                }

                // If blacklist blocked everything, reset and allow everything
                if (cycle.Count == 0)
                {
                    ControllerMain.LogWarn("[CyclePicker] Blacklist blocked all. Resetting.");
                    for (int i = 0; i < length; i++) cycle.Add(i);
                }

                FisherYatesShuffle(cycle);

                // Prevent immediate repeat if possible
                if (cycle.Count > 1 && lastUsed >= 0 && cycle[0] == lastUsed)
                {
                    int j = Random.Range(1, cycle.Count);
                    (cycle[0], cycle[j]) = (cycle[j], cycle[0]);
                }
            }

            if (cycle.Count == 0) return -1;

            int pick = cycle[0];
            cycle.RemoveAt(0);
            newLastUsed = pick;
            
            ControllerMain.LogInfo($"[CyclePicker] Picked {pick}. Remaining in cycle: {cycle.Count}");
            return pick;
        }

        public static void FisherYatesShuffle(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
