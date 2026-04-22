using System;
using UnityEngine;

namespace MECSense
{
    /// <summary>
    /// 静息态基线校准器：采集用户放松状态下的生理/行为基线值。
    /// 流程：30秒采集 → 去除前5秒过渡期 → 取剩余25秒均值 → 输出7通道基线。
    /// 使用方式：调用 StartCalibration() 开始，通过 IsCalibrated 检查完成状态，
    /// 通过 BaselineData 获取结果，或监听 OnCalibrationCompleted 事件。
    /// </summary>
    public class BaselineCalibrator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        FeatureExtractor m_FeatureExtractor;

        [Header("Calibration Settings")]
        [SerializeField]
        [Tooltip("是否在场景加载后自动开始校准")]
        bool m_AutoStartOnEnable = true;

        [SerializeField]
        [Tooltip("自动开始的延迟时间（秒），等待系统稳定")]
        float m_AutoStartDelay = 3f;

        [SerializeField]
        [Tooltip("总校准时长（秒）")]
        float m_CalibrationDuration = 10f;

        [SerializeField]
        [Tooltip("丢弃的过渡期时长（秒）")]
        float m_TransitionDiscard = 5f;

        [SerializeField]
        [Tooltip("采样间隔（秒），每 N 秒采一个快照")]
        float m_SampleInterval = 0.33f; // ~每帧一次（配合FeatureExtractor）

        [Header("Events")]
        [SerializeField]
        UnityEngine.Events.UnityEvent<BaselineData> m_OnCalibrationCompleted;

        #region 公开属性

        /// <summary>是否已完成校准</summary>
        public bool IsCalibrated => m_State == CalibrationState.Completed;

        /// <summary>当前校准进度 (0~1)</summary>
        public float Progress => Mathf.Clamp01(m_ElapsedTime / m_CalibrationDuration);

        /// <summary>当前状态</summary>
        public CalibrationState State => m_State;

        /// <summary>校准后的基线数据</summary>
        public BaselineData CurrentBaseline => m_BaselineResult;

        #endregion

        #region 内部类型

        public enum CalibrationState
        {
            Idle,
            Collecting,
            Completed,
            Failed
        }

        /// <summary>
        /// 7通道基线数据：每个通道记录均值和标准差
        /// </summary>
        [Serializable]
        public struct BaselineData
        {
            // 0: 面部特征 (52维)
            public float[] FaceMean;
            public float[] FaceStd;

            // 1: 注视发散度
            public float GazeVarianceMean;
            public float GazeVarianceStd;

            // 2: 瞳孔直径变化率
            public float PupilRateMean;
            public float PupilRateStd;

            // 3: 眨眼频率 (次/分钟)
            public float BlinkRateMean;
            public float BlinkRateStd;

            // 4: 手部微颤 (mm/s)
            public float HandJitterMean;
            public float HandJitterStd;

            // 5: 笔触间停顿时长 (秒)
            public float StrokePauseMean;
            public float StrokePauseStd;

            // 6: 撤销频率 + 模式切换频率
            public float UndoRateMean;
            public float ModeSwitchRateMean;

            /// <summary>总采样数（有效样本）</summary>
            public int ValidSampleCount;
        }

        #endregion

        #region 内部状态

        CalibrationState m_State = CalibrationState.Idle;
        float m_ElapsedTime;
        float m_LastSampleTime;
        int m_TotalSamples;
        int m_ValidSamples; // 过渡期之后的样本

        // 累积器（用于在线计算均值/方差）
        SampleAccumulator[] m_Accumulators;
        BaselineData m_BaselineResult;

        // 最小有效样本数
        const int k_MinValidSamples = 10;

        #endregion

        void Awake()
        {
            if (m_FeatureExtractor == null)
                m_FeatureExtractor = FindAnyObjectByType<FeatureExtractor>();
        }

        void OnEnable()
        {
            if (m_AutoStartOnEnable)
                Invoke(nameof(StartCalibration), m_AutoStartDelay);
        }

        void OnDisable()
        {
            CancelInvoke(nameof(StartCalibration));
        }

        void Update()
        {
            if (m_State != CalibrationState.Collecting)
                return;

            m_ElapsedTime += Time.deltaTime;

            // 定期采样
            if (m_ElapsedTime - m_LastSampleTime >= m_SampleInterval)
            {
                TakeSample();
                m_LastSampleTime = m_ElapsedTime;
            }

            // 检查是否完成
            if (m_ElapsedTime >= m_CalibrationDuration)
            {
                FinalizeCalibration();
            }
        }

        #region 公开接口

        /// <summary>
        /// 开始校准。如果已在采集中则忽略。
        /// </summary>
        public void StartCalibration()
        {
            if (m_State == CalibrationState.Collecting)
            {
                Debug.LogWarning("[MECSense] 校准正在进行中");
                return;
            }

            if (m_FeatureExtractor == null)
            {
                Debug.LogError("[MECSense] BaselineCalibrator 缺少 FeatureExtractor 引用");
                m_State = CalibrationState.Failed;
                return;
            }

            ResetState();
            m_State = CalibrationState.Collecting;
            m_ElapsedTime = 0f;
            m_LastSampleTime = 0f;
            m_TotalSamples = 0;
            m_ValidSamples = 0;

            InitializeAccumulators();

            Debug.Log($"[MECSense] 基线校准开始: 总时长={m_CalibrationDuration}s, 过渡期={m_TransitionDiscard}s, 采样间隔={m_SampleInterval}s");
        }

        /// <summary>
        /// 重置校准状态，允许重新校准。
        /// </summary>
        public void Reset()
        {
            ResetState();
            m_State = CalibrationState.Idle;
        }

        #endregion

        #region 内部实现

        void ResetState()
        {
            m_BaselineResult = default;
            m_Accumulators = null;
            m_ElapsedTime = 0f;
        }

        void InitializeAccumulators()
        {
            // 7个累积器对应7个通道
            m_Accumulators = new SampleAccumulator[7];
            m_Accumulators[0] = new SampleAccumulator(52);   // 面部
            m_Accumulators[1] = new SampleAccumulator(1);     // 注视发散度
            m_Accumulators[2] = new SampleAccumulator(1);     // 瞳孔变化率
            m_Accumulators[3] = new SampleAccumulator(1);     // 眨眼频率
            m_Accumulators[4] = new SampleAccumulator(1);     // 手部微颤
            m_Accumulators[5] = new SampleAccumulator(1);     // 笔触停顿
            m_Accumulators[6] = new SampleAccumulator(2);     // 撤销+模式切换
        }

        void TakeSample()
        {
            if (!m_FeatureExtractor.IsFeatureReady || m_FeatureExtractor.FeatureVector == null)
                return;

            float[] fv = m_FeatureExtractor.FeatureVector;
            m_TotalSamples++;

            // 判断是否在过渡期内
            bool inTransition = m_ElapsedTime < m_TransitionDiscard;
            if (!inTransition)
                m_ValidSamples++;

            // 解析特征向量并写入各通道累加器
            // [52 face] [3 eye] [1 hand] [3 interaction] = 59

            // Ch0: 面部 (52维)
            for (int i = 0; i < 52; i++)
                m_Accumulators[0].Add(i, fv[i], !inTransition);

            // Ch1: 注视发散度 (fv[52])
            m_Accumulators[1].Add(0, fv[52], !inTransition);

            // Ch2: 瞳孔变化率 (fv[53])
            m_Accumulators[2].Add(0, fv[53], !inTransition);

            // Ch3: 眨眼频率 (fv[54])
            m_Accumulators[3].Add(0, fv[54], !inTransition);

            // Ch4: 手部微颤 (fv[55])
            m_Accumulators[4].Add(0, fv[55], !inTransition);

            // Ch5: 笔触停顿 (fv[56])
            m_Accumulators[5].Add(0, fv[56], !inTransition);

            // Ch6: 撤销率(fv[57]) + 模式切换率(fv[58])
            m_Accumulators[6].Add(0, fv[57], !inTransition);
            m_Accumulators[6].Add(1, fv[58], !inTransition);
        }

        void FinalizeCalibration()
        {
            if (m_ValidSamples < k_MinValidSamples)
            {
                Debug.LogWarning($"[MECSense] 校准失败: 有效样本不足 ({m_ValidSamples} < {k_MinValidSamples})");
                m_State = CalibrationState.Failed;
                return;
            }

            // 从累加器提取基线数据
            var b = new BaselineData();

            // Ch0: 面部
            b.FaceMean = m_Accumulators[0].GetMeans(52);
            b.FaceStd = m_Accumulators[0].GetStds(52);

            // Ch1-4: 标量
            b.GazeVarianceMean = m_Accumulators[1].GetMean(0);
            b.GazeVarianceStd = m_Accumulators[1].GetStd(0);

            b.PupilRateMean = m_Accumulators[2].GetMean(0);
            b.PupilRateStd = m_Accumulators[2].GetStd(0);

            b.BlinkRateMean = m_Accumulators[3].GetMean(0);
            b.BlinkRateStd = m_Accumulators[3].GetStd(0);

            b.HandJitterMean = m_Accumulators[4].GetMean(0);
            b.HandJitterStd = m_Accumulators[4].GetStd(0);

            b.StrokePauseMean = m_Accumulators[5].GetMean(0);
            b.StrokePauseStd = m_Accumulators[5].GetStd(0);

            b.UndoRateMean = m_Accumulators[6].GetMean(0);
            b.ModeSwitchRateMean = m_Accumulators[6].GetMean(1);

            b.ValidSampleCount = m_ValidSamples;

            m_BaselineResult = b;
            m_State = CalibrationState.Completed;

            Debug.Log($"[MECSense] 基线校准完成: 有效样本={m_ValidSamples}, " +
                      $"jitter_base={b.HandJitterMean:F2}±{b.HandJitterStd:F2}, " +
                      $"blink_base={b.BlinkRateMean:F1}/min");

            m_OnCalibrationCompleted?.Invoke(b);
        }

        #endregion

        #region 在线统计累加器

        /// <summary>
        /// Welford 在线算法：支持多维度、可选择性计入/不计入的均值/方差计算
        /// </summary>
        class SampleAccumulator
        {
            double[] m_Mean;
            double[] m_M2;      // 二阶中心矩
            int[] m_Count;
            int m_Dims;
            int m_ValidCount;   // 仅统计有效（非过渡期）样本

            public SampleAccumulator(int dimensions)
            {
                m_Dims = dimensions;
                m_Mean = new double[dimensions];
                m_M2 = new double[dimensions];
                m_Count = new int[dimensions];
                m_ValidCount = 0;
            }

            public void Add(int dim, float value, bool isValid)
            {
                // 无论是否有效都更新运行统计（用于显示）
                m_Count[dim]++;
                double delta = value - m_Mean[dim];
                m_Mean[dim] += delta / m_Count[dim];
                double delta2 = value - m_Mean[dim];
                m_M2[dim] += delta * delta2;

                if (isValid)
                    m_ValidCount++;
            }

            public float GetMean(int dim)
            {
                return m_Count[dim] > 0 ? (float)m_Mean[dim] : 0f;
            }

            public float GetStd(int dim)
            {
                if (m_Count[dim] < 2) return 0f;
                float variance = (float)(m_M2[dim] / (m_Count[dim] - 1));
                return Mathf.Sqrt(Mathf.Max(variance, 1e-8f)); // 防除零保护
            }

            public float[] GetMeans(int dims)
            {
                var result = new float[dims];
                for (int i = 0; i < dims && i < m_Dims; i++)
                    result[i] = GetMean(i);
                return result;
            }

            public float[] GetStds(int dims)
            {
                var result = new float[dims];
                for (int i = 0; i < dims && i < m_Dims; i++)
                    result[i] = GetStd(i);
                return result;
            }

            public int ValidCount => m_ValidCount;
        }

        #endregion
    }
}
