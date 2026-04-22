using System;

namespace MECSense
{
    /// <summary>
    /// 7 类情绪标签
    /// </summary>
    public enum EmotionLabel
    {
        Happy,
        Sad,
        Angry,
        Surprised,
        Fear,
        Disgust,
        Neutral
    }

    /// <summary>
    /// 认知负荷等级
    /// </summary>
    public enum CognitiveLoadLevel
    {
        Low,
        Medium,
        High
    }

    /// <summary>
    /// 交互事件类型
    /// </summary>
    public enum InteractionEventType
    {
        StrokeEnd,
        Undo,
        ModeSwitch
    }

    /// <summary>
    /// 交互事件
    /// </summary>
    [Serializable]
    public struct InteractionEvent
    {
        public InteractionEventType Type;
        public long Timestamp;
    }

    /// <summary>
    /// 用户状态广播载荷
    /// </summary>
    [Serializable]
    public struct UserStatePayload
    {
        public EmotionLabel Emotion;
        public float[] EmotionProbabilities; // 7类
        public float FatigueIndex;           // 0~1
        public CognitiveLoadLevel LoadLevel; // Low/Medium/High
        public float HandJitterRate;         // mm/s
    }
}
