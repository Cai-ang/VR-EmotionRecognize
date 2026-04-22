using System.Collections.Generic;
using UnityEngine;
using Unity.Barracuda;

namespace MECSense
{
    /// <summary>
    /// 认知负荷推理器：接收 59 维特征向量，输出情绪标签、疲劳指数和认知负荷等级。
    /// 使用 Barracuda 加载 ONNX 模型，含双头输出 + 时序平滑。
    /// </summary>
    public class CognitiveLoadInferencer : MonoBehaviour
    {
        [Header("Model")]
        [SerializeField]
        [Tooltip("ONNX 模型资产")]
        NNModel m_ModelAsset;

        [Header("References")]
        [SerializeField]
        [Tooltip("特征提取器")]
        FeatureExtractor m_FeatureExtractor;

        [Header("Smoothing")]
        [SerializeField]
        [Tooltip("情绪多数投票窗口大小")]
        int m_EmotionVoteSize = 10;

        [SerializeField]
        [Tooltip("疲劳指数 EMA 平滑系数 α")]
        [Range(0f, 1f)]
        float m_FatigueAlpha = 0.3f;

        [Header("Inference")]
        [SerializeField]
        [Tooltip("推理间隔帧数（30帧 ≈ 0.33秒）")]
        int m_InferenceInterval = 30;

        [Header("Events")]
        [SerializeField]
        [Tooltip("每次推理完成后广播状态载荷")]
        UnityEngine.Events.UnityEvent<UserStatePayload> m_OnStateUpdated;

        #region 公开属性

        /// <summary>最新情绪标签</summary>
        public EmotionLabel Emotion => m_SmoothedEmotion;

        /// <summary>最新疲劳指数 [0, 1]</summary>
        public float FatigueIndex => m_SmoothedFatigue;

        /// <summary>最新认知负荷等级</summary>
        public CognitiveLoadLevel LoadLevel => m_LoadLevel;

        /// <summary>最新状态载荷</summary>
        public UserStatePayload CurrentPayload => m_CurrentPayload;

        /// <summary>手部微颤率</summary>
        public float HandJitterRate => m_FeatureExtractor != null ? m_FeatureExtractor.HandJitterRate : 0f;

        #endregion

        #region 内部状态

        Model m_RuntimeModel;
        IWorker m_Worker;
        bool m_Initialized;

        EmotionLabel m_SmoothedEmotion = EmotionLabel.Neutral;
        float m_SmoothedFatigue;
        bool m_FatigueInitialized;
        CognitiveLoadLevel m_LoadLevel = CognitiveLoadLevel.Low;
        UserStatePayload m_CurrentPayload;
        int m_FrameCount;

        // 情绪多数投票
        Queue<EmotionLabel> m_EmotionHistory = new Queue<EmotionLabel>();

        // 情绪标签索引映射
        static readonly string[] k_EmotionNames = {
            "Happy", "Sad", "Angry", "Surprised", "Fear", "Disgust", "Neutral"
        };

        #endregion

        void Start()
        {
            if (m_FeatureExtractor == null)
                m_FeatureExtractor = FindAnyObjectByType<FeatureExtractor>();

            if (m_ModelAsset != null)
            {
                try
                {
                    m_RuntimeModel = ModelLoader.Load(m_ModelAsset);
                    m_Worker = WorkerFactory.CreateWorker(WorkerFactory.Type.Compute, m_RuntimeModel);
                    m_Initialized = true;
                    Debug.Log("[MECSense] 认知负荷模型加载成功");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[MECSense] 模型加载失败: {e.Message}");
                }
            }
            else
            {
                Debug.LogWarning("[MECSense] 未指定 ONNX 模型，推理不可用");
            }
        }

        void Update()
        {
            m_FrameCount++;

            if (m_FrameCount % m_InferenceInterval != 0)
                return;

            if (m_FeatureExtractor == null || !m_FeatureExtractor.IsFeatureReady)
                return;

            RunInference();
        }

        void RunInference()
        {
            float[] features = m_FeatureExtractor.FeatureVector;
            if (features == null || features.Length != 59)
                return;

            if (!m_Initialized || m_Worker == null)
                return;

            try
            {
                using Tensor input = new Tensor(1, 59, features);
                m_Worker.Execute(input);

                // 双头输出
                using Tensor emotionOutput = m_Worker.PeekOutput("emotion_output");
                using Tensor fatigueOutput = m_Worker.PeekOutput("fatigue_output");

                float[] emotionProbs = emotionOutput.data.Download(emotionOutput.shape);
                float[] fatigueRaw = fatigueOutput.data.Download(fatigueOutput.shape);

                // 情绪：多数投票
                EmotionLabel rawEmotion = ArgMaxEmotion(emotionProbs);
                m_EmotionHistory.Enqueue(rawEmotion);
                if (m_EmotionHistory.Count > m_EmotionVoteSize)
                    m_EmotionHistory.Dequeue();
                m_SmoothedEmotion = MajorityVote();

                // 疲劳：EMA 平滑
                float rawFatigue = fatigueRaw[0];
                if (!m_FatigueInitialized)
                {
                    m_SmoothedFatigue = rawFatigue;
                    m_FatigueInitialized = true;
                }
                else
                {
                    m_SmoothedFatigue = m_FatigueAlpha * rawFatigue + (1f - m_FatigueAlpha) * m_SmoothedFatigue;
                }

                // 认知负荷映射
                if (m_SmoothedFatigue < 0.3f)
                    m_LoadLevel = CognitiveLoadLevel.Low;
                else if (m_SmoothedFatigue < 0.6f)
                    m_LoadLevel = CognitiveLoadLevel.Medium;
                else
                    m_LoadLevel = CognitiveLoadLevel.High;

                // 封装载荷
                m_CurrentPayload = new UserStatePayload
                {
                    Emotion = m_SmoothedEmotion,
                    EmotionProbabilities = emotionProbs,
                    FatigueIndex = m_SmoothedFatigue,
                    LoadLevel = m_LoadLevel,
                    HandJitterRate = m_FeatureExtractor.HandJitterRate
                };

                // 广播
                m_OnStateUpdated?.Invoke(m_CurrentPayload);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MECSense] 推理失败: {e.Message}");
            }
        }

        EmotionLabel ArgMaxEmotion(float[] probs)
        {
            int bestIdx = 0;
            float bestVal = probs[0];
            for (int i = 1; i < probs.Length && i < 7; i++)
            {
                if (probs[i] > bestVal)
                {
                    bestVal = probs[i];
                    bestIdx = i;
                }
            }
            return (EmotionLabel)bestIdx;
        }

        EmotionLabel MajorityVote()
        {
            var counts = new Dictionary<EmotionLabel, int>();
            foreach (var e in m_EmotionHistory)
            {
                if (!counts.ContainsKey(e)) counts[e] = 0;
                counts[e]++;
            }

            EmotionLabel best = EmotionLabel.Neutral;
            int bestCount = 0;
            foreach (var kv in counts)
            {
                if (kv.Value > bestCount)
                {
                    bestCount = kv.Value;
                    best = kv.Key;
                }
            }
            return best;
        }

        void OnDestroy()
        {
            m_Worker?.Dispose();
        }
    }
}
