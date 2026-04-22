using System;
using UnityEngine;

namespace MECSense
{
    /// <summary>
    /// 认知负荷估算器（无模型规则引擎）。
    /// 接收 FeatureExtractor 的 59 维特征 + BaselineCalibrator 的基线数据，
    /// 通过 7 通道加权规则 → 归一化偏离度 → EMA 平滑(α=0.3) → 三级负荷判定。
    /// 
    /// 不依赖 ONNX 模型，适用于无训练数据时的轻量级实时推理。
    /// </summary>
    public class CognitiveLoadEstimator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        FeatureExtractor m_FeatureExtractor;

        [SerializeField]
        BaselineCalibrator m_BaselineCalibrator;

        [Header("Channel Weights (总和 = 1.0)")]
        [SerializeField]
        [Tooltip("Ch0: 面部表情偏离权重")]
        [Range(0f, 1f)]
        float m_WeightFace = 0.15f;

        [SerializeField]
        [Tooltip("Ch1: 注视发散度权重")]
        [Range(0f, 1f)]
        float m_WeightGazeVariance = 0.18f;

        [SerializeField]
        [Tooltip("Ch2: 瞳孔直径变化率权重")]
        [Range(0f, 1f)]
        float m_WeightPupilRate = 0.12f;

        [SerializeField]
        [Tooltip("Ch3: 眨眼频率权重")]
        [Range(0f, 1f)]
        float m_WeightBlinkRate = 0.10f;

        [SerializeField]
        [Tooltip("Ch4: 手部微颤权重")]
        [Range(0f, 1f)]
        float m_WeightHandJitter = 0.20f;

        [SerializeField]
        [Tooltip("Ch5: 笔触停顿时长权重")]
        [Range(0f, 1f)]
        float m_WeightStrokePause = 0.13f;

        [SerializeField]
        [Tooltip("Ch6: 撤销+模式切换频率权重")]
        [Range(0f, 1f)]
        float m_WeightCorrectionRate = 0.12f;

        [Header("EMA Smoothing")]
        [SerializeField]
        [Tooltip("指数移动平均平滑系数 α (越大响应越快，越小越平滑)")]
        [Range(0.05f, 0.5f)]
        float m_EMAAlpha = 0.3f;

        [Header("Thresholds")]
        [SerializeField]
        [Tooltip("Low→Medium 阈值")]
        float m_LowToMediumThreshold = 0.35f;

        [SerializeField]
        [Tooltip("Medium→High 阈值")]
        float m_MediumToHighThreshold = 0.65f;

        [Header("Inference")]
        [SerializeField]
        [Tooltip("推理间隔帧数")]
        int m_InferenceInterval = 30; // 与 FeatureExtractor 对齐

        [Header("Events")]
        [SerializeField]
        UnityEngine.Events.UnityEvent<CognitiveLoadResult> m_OnLoadUpdated;

        #region 公开属性

        /// <summary>当前认知负荷等级</summary>
        public CognitiveLoadLevel LoadLevel => m_CurrentLevel;

        /// <summary>当前原始综合得分 (0~1+, 越高负荷越大)</summary>
        public float RawScore => m_RawScore;

        /// <summary>EMA 平滑后的得分</summary>
        public float SmoothedScore => m_SmoothedScore;

        /// <summary>各通道偏离度详情</summary>
        public float[] ChannelDeviations => m_ChannelDeviationCopy;

        /// <summary>最新结果载荷</summary>
        public CognitiveLoadResult CurrentResult => m_CurrentResult;

        /// <summary>是否有有效基线可用</summary>
        public bool HasBaseline => m_BaselineCalibrator != null && m_BaselineCalibrator.IsCalibrated;

        #endregion

        #region 内部类型

        /// <summary>
        /// 单次推理结果
        /// </summary>
        [Serializable]
        public struct CognitiveLoadResult
        {
            public CognitiveLoadLevel Level;
            public float RawScore;
            public float SmoothedScore;
            public float[] ChannelDeviations; // 7通道
            public float Timestamp;
            public bool BaselineAvailable;
        }

        #endregion

        #region 内部状态

        CognitiveLoadLevel m_CurrentLevel = CognitiveLoadLevel.Low;
        float m_RawScore;
        float m_SmoothedScore;
        bool m_SmoothedInitialized;
        float[] m_ChannelDeviation = new float[7];      // 内部工作数组
        float[] m_ChannelDeviationCopy = new float[7];   // 公开副本
        CognitiveLoadResult m_CurrentResult;
        int m_FrameCount;

        // 防除零下界
        const float k_Epsilon = 1e-6f;

        #endregion

        void Awake()
        {
            if (m_FeatureExtractor == null)
                m_FeatureExtractor = FindAnyObjectByType<FeatureExtractor>();
            if (m_BaselineCalibrator == null)
                m_BaselineCalibrator = FindAnyObjectByType<BaselineCalibrator>();

            ValidateWeights();
        }

        void Update()
        {
            m_FrameCount++;

            if (m_FrameCount % m_InferenceInterval != 0)
                return;

            RunInference();
        }

        void RunInference()
        {
            // 前置检查
            if (m_FeatureExtractor == null || !m_FeatureExtractor.IsFeatureReady)
                return;
            if (!HasBaseline)
            {
                // 无基线时输出默认低负荷
                OutputDefault();
                return;
            }

            float[] fv = m_FeatureExtractor.FeatureVector;
            var baseline = m_BaselineCalibrator.CurrentBaseline;

            // ===== 7通道偏离度计算 =====

            // Ch0: 面部特征偏离 (52维取平均偏离度)
            m_ChannelDeviation[0] = ComputeFaceDeviation(fv, baseline);

            // Ch1: 注视发散度偏离
            m_ChannelDeviation[1] = ComputeScalarDeviation(fv[52], baseline.GazeVarianceMean, baseline.GazeVarianceStd);

            // Ch2: 瞳孔变化率偏离
            m_ChannelDeviation[2] = ComputeScalarDeviation(fv[53], baseline.PupilRateMean, baseline.PupilRateStd);

            // Ch3: 眨眼频率偏离
            m_ChannelDeviation[3] = ComputeScalarDeviation(fv[54], baseline.BlinkRateMean, baseline.BlinkRateStd);

            // Ch4: 手部微颤偏离
            m_ChannelDeviation[4] = ComputeScalarDeviation(fv[55], baseline.HandJitterMean, baseline.HandJitterStd);

            // Ch5: 笔触停顿偏离
            m_ChannelDeviation[5] = ComputeScalarDeviation(fv[56], baseline.StrokePauseMean, baseline.StrokePauseStd);

            // Ch6: 撤销+模式切换偏离 (两个子通道平均)
            float undoDev = ComputeScalarDeviationNoBaseline(fv[57]); // 基准为0（正常不撤销）
            float modeDev = ComputeScalarDeviationNoBaseline(fv[58]); // 基准为0（正常不切换）
            m_ChannelDeviation[6] = (undoDev + modeDev) * 0.5f;

            // ===== 加权求和 =====
            m_RawScore =
                m_ChannelDeviation[0] * m_WeightFace +
                m_ChannelDeviation[1] * m_WeightGazeVariance +
                m_ChannelDeviation[2] * m_WeightPupilRate +
                m_ChannelDeviation[3] * m_WeightBlinkRate +
                m_ChannelDeviation[4] * m_WeightHandJitter +
                m_ChannelDeviation[5] * m_WeightStrokePause +
                m_ChannelDeviation[6] * m_WeightCorrectionRate;

            // ===== EMA 平滑 =====
            if (!m_SmoothedInitialized)
            {
                m_SmoothedScore = m_RawScore;
                m_SmoothedInitialized = true;
            }
            else
            {
                m_SmoothedScore = m_EMAAlpha * m_RawScore + (1f - m_EMAAlpha) * m_SmoothedScore;
            }

            // ===== 三级判定 =====
            m_Classify();

            // ===== 输出结果 =====
            Array.Copy(m_ChannelDeviation, m_ChannelDeviationCopy, 7);
            m_CurrentResult = new CognitiveLoadResult
            {
                Level = m_CurrentLevel,
                RawScore = m_RawScore,
                SmoothedScore = m_SmoothedScore,
                ChannelDeviations = (float[])m_ChannelDeviationCopy.Clone(),
                Timestamp = Time.time,
                BaselineAvailable = true
            };

            m_OnLoadUpdated?.Invoke(m_CurrentResult);

            if (m_EnableDebugLog)
            {
                Debug.Log($"[MECSense] CogLoad: raw={m_RawScore:F3}, smooth={m_SmoothedScore:F3}, " +
                          $"level={m_CurrentLevel}, " +
                          $"ch=[{m_ChannelDeviation[0]:F2},{m_ChannelDeviation[1]:F2},{m_ChannelDeviation[2]:F2}," +
                          $"{m_ChannelDeviation[3]:F2},{m_ChannelDeviation[4]:F2},{m_ChannelDeviation[5]:F2},{m_ChannelDeviation[6]:F2}]");
            }
        }

        #region 偏离度计算

        /// <summary>
        /// 面部特征52维偏离度：对各维分别计算Z-score绝对值后取均值
        /// </summary>
        float ComputeFaceDeviation(float[] fv, BaselineCalibrator.BaselineData baseLine)
        {
            if (baseLine.FaceMean == null || baseLine.FaceMean.Length == 0)
                return 0f;

            int dims = Mathf.Min(52, baseLine.FaceMean.Length);
            float sumDev = 0f;
            int validDims = 0;

            for (int i = 0; i < dims; i++)
            {
                float std = Mathf.Max(baseLine.FaceStd[i], k_Epsilon);
                float z = Mathf.Abs((fv[i] - baseLine.FaceMean[i]) / std);
                sumDev += z;
                validDims++;
            }

            return validDims > 0 ? sumDev / validDims : 0f;
        }

        /// <summary>
        /// 标量偏离度：|value - mean| / max(std, ε)，结果裁剪到 [0, 3]
        /// </summary>
        float ComputeScalarDeviation(float value, float mean, float std)
        {
            float deviation = Mathf.Abs(value - mean) / Mathf.Max(std, k_Epsilon);
            return Mathf.Clamp(deviation, 0f, 3f); // 裁剪极端异常值
        }

        /// <summary>
        /// 无基线的标量偏离（用于撤销率等，基准值为0）
        /// </summary>
        float ComputeScalarDeviationNoBaseline(float value)
        {
            // 使用固定经验阈值：假设 >3次/分钟为高负荷
            return Mathf.Clamp(value / 3f, 0f, 3f);
        }

        #endregion

        #region 判定逻辑

        void m_Classify()
        {
            float score = m_SmoothedScore;

            if (score < m_LowToMediumThreshold)
                m_CurrentLevel = CognitiveLoadLevel.Low;
            else if (score < m_MediumToHighThreshold)
                m_CurrentLevel = CognitiveLoadLevel.Medium;
            else
                m_CurrentLevel = CognitiveLoadLevel.High;
        }

        void OutputDefault()
        {
            m_RawScore = 0f;
            m_SmoothedScore = 0f;
            for (int i = 0; i < 7; i++) m_ChannelDeviation[i] = 0f;
            m_CurrentLevel = CognitiveLoadLevel.Low;
            m_CurrentResult = new CognitiveLoadResult
            {
                Level = CognitiveLoadLevel.Low,
                RawScore = 0f,
                SmoothedScore = 0f,
                ChannelDeviations = new float[7],
                Timestamp = Time.time,
                BaselineAvailable = false
            };
        }

        #endregion

        #region 权重校验

        [Header("Debug")]
        [SerializeField]
        bool m_EnableDebugLog = false;

        void ValidateWeights()
        {
            float total = m_WeightFace + m_WeightGazeVariance + m_WeightPupilRate +
                          m_WeightBlinkRate + m_WeightHandJitter + m_WeightStrokePause +
                          m_WeightCorrectionRate;

            if (Mathf.Abs(total - 1f) > 0.01f)
            {
                Debug.LogWarning($"[MECSense] CognitiveLoadEstimator 权重总和不等于1.0 (={total:F3})，将自动归一化");
                NormalizeWeights(total);
            }
        }

        void NormalizeWeights(float total)
        {
            m_WeightFace /= total;
            m_WeightGazeVariance /= total;
            m_WeightPupilRate /= total;
            m_WeightBlinkRate /= total;
            m_WeightHandJitter /= total;
            m_WeightStrokePause /= total;
            m_WeightCorrectionRate /= total;
        }

        #endregion
    }
}
