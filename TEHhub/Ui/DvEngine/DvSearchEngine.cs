// <copyright file="DvSearchEngine.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui.DvEngine
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    ///     Performs fast, metadata-only fuzzy search over registered DV v2 navigation nodes.
    ///     Guaranteed to perform zero remote memory reads and zero ObjectResolver calls.
    /// </summary>
    public static class DvSearchEngine
    {
        /// <summary>
        ///     Searches registered nodes for matching display names, categories, paths, or tags.
        /// </summary>
        /// <param name="query">User search input string.</param>
        /// <param name="candidates">Candidate nodes to search over (defaults to DvRegistry.AllNodes).</param>
        /// <returns>Ranked list of matching navigation nodes.</returns>
        public static List<DvNavNode> Search(string query, IEnumerable<DvNavNode>? candidates = null)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<DvNavNode>();
            }

            var pool = candidates ?? DvRegistry.AllNodes;
            var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Length == 0)
            {
                return new List<DvNavNode>();
            }

            var scoredResults = new List<(DvNavNode Node, int Score)>();

            foreach (var node in pool)
            {
                int totalScore = 0;
                bool allTermsMatched = true;

                foreach (var term in terms)
                {
                    int termScore = CalculateTermScore(node, term);
                    if (termScore <= 0)
                    {
                        allTermsMatched = false;
                        break;
                    }

                    totalScore += termScore;
                }

                if (allTermsMatched && totalScore > 0)
                {
                    scoredResults.Add((node, totalScore));
                }
            }

            return scoredResults
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Node.DisplayName.Length)
                .Select(r => r.Node)
                .ToList();
        }

        private static int CalculateTermScore(DvNavNode node, string term)
        {
            int score = 0;

            // 1. DisplayName matching
            if (string.Equals(node.DisplayName, term, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (node.DisplayName.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }
            else if (node.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            // 2. Tag / Alias matching
            foreach (var tag in node.Tags)
            {
                if (string.Equals(tag, term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 60;
                }
                else if (tag.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 35;
                }
                else if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }
            }

            // 3. Category / Path matching
            if (node.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }

            if (node.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            // 4. Stable Id matching
            if (node.Id.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            return score;
        }
    }
}