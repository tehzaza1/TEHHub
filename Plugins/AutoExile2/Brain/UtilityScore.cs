// <copyright file="UtilityScore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    /// <summary>
    /// Represents a calculated utility/desirability score for a potential bot goal.
    /// Used by the Cognitive Central Brain to arbitrate decisions dynamically.
    /// </summary>
    public readonly struct UtilityScore
    {
        public BotGoalType GoalType { get; }

        public float Score { get; }

        public string Reason { get; }

        public UtilityScore(BotGoalType goalType, float score, string reason)
        {
            this.GoalType = goalType;
            this.Score = score;
            this.Reason = reason;
        }

        public override string ToString() => $"[{this.GoalType}] Score: {this.Score:F1} ({this.Reason})";
    }
}
