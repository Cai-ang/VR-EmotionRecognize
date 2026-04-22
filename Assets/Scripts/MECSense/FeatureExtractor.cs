using System;
using System.Collections.Generic;
using UnityEngine;

namespace MECSense
{
    /// <summary>
    /// 特征提取器：每 30 渲染帧（~0.33秒）输出一个 59 维融合特征向量
    /// [52 面部 + 3 眼动 + 1 手部微颤 + 3 交互行为]
    /// </summary>
    public class FeatureExtractor : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        MultiChannelDataCollector m_DataCollector;

        [Header("Viseme 索引配置（从72维中剔除的20维）")]
        [Tooltip("ARKit/Oculus 标准 Viseme 索引: viseme_aa(0..19) 对应 BlendShape 索引")]
        [SerializeField]
        int[] m_VisemeIndices = {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
            10, 11, 12, 13, 14, 15, 16, 17, 18, 19
        };

        [Header("滤波参数")]
        [SerializeField]
        [Tooltip("手部微颤低通截止频率 (Hz)")]
        float m_LowpassCutoff = 4f;

        [SerializeField]
        [Tooltip("渲染帧率 (Hz)")]
        float m_RenderFPS = 90f;

        [Header("推理周期")]
        [SerializeField]
        [Tooltip("每 N 帧推理一次")]
        int m_InferenceIntervalFrames = 30;

        [Header("Debug")]
        [SerializeField]
        bool m_EnableDebugLog = false;

        #region 公开属性

        /// <summary>
        /// 59 维融合特征向量（每次推理更新）
        /// </summary>
        public float[] FeatureVector => m_FeatureVector;

        /// <summary>
        /// 特征是否就绪（可用于推理）
        /// </summary>
        public bool IsFeatureReady => m_FeatureReady;

        /// <summary>
        /// 当前手部微颤率 (mm/s)
        /// </summary>
        public float HandJitterRate => m_JitterRate;

        #endregion

        #region 内部状态

        // 输出
        float[] m_FeatureVector = new float[59];
        bool m_FeatureReady;

        // 面部特征缓冲 (1秒滑动窗口)
        const int k_FaceWindowSize = 90; // 90帧 = 1秒 @90Hz
        float[] m_FaceRunningMean; // 52维运行均值
        float[] m_FaceRunningVar;  // 52维运行方差
        float m_FaceUpdateCount;
        Queue<float[]> m_FaceWindow = new Queue<float[]>();

        // 眼动特征缓冲
        const int k_GazeWindowSize = 90;    // 1秒
        const int k_BlinkWindowSize = 900;  // 10秒
        Queue<Vector3> m_GazeWindow = new Queue<Vector3>();
        Queue<float> m_PupilHistory = new Queue<float>();
        Queue<(long frame, bool blink)> m_BlinkWindow = new Queue<(long, bool)>();

        // 手部微颤缓冲
        const int k_HandWindowSize = 90; // 1秒
        float[][] m_HandHistory = new float[3][]; // 3根指尖
        int m_HandHistoryCount;
        Vector3[] m_HandPrevPositions = new Vector3[3];
        bool m_HandPrevInitialized;

        // 二阶巴特沃斯低通滤波器系数
        float[] m_LowpassB; // 分子 [b0, b1, b2]
        float[] m_LowpassA; // 分母 [1, a1, a2] (a0=1 归一化)
        // 每根指尖 × 每轴独立滤波器状态: [3 fingertip][3 axis][4 state]
        float[][][] m_FilterState;
        Vector3[] m_FilteredPositions = new Vector3[3];

        // 指尖关节索引
        static readonly int[] k_FingertipJoints = {
            (int)UnityEngine.XR.Hands.XRHandJointID.IndexTip,
            (int)UnityEngine.XR.Hands.XRHandJointID.MiddleTip,
            (int)UnityEngine.XR.Hands.XRHandJointID.RingTip
        };

        // 交互行为事件缓冲
        const int k_StrokeWindow = 900;  // 10秒
        const int k_UndoWindow = 2700;   // 30秒
        const int k_ModeSwitchWindow = 5400; // 60秒
        Queue<long> m_StrokeEndTimes = new Queue<long>();
        Queue<long> m_UndoTimes = new Queue<long>();
        Queue<long> m_ModeSwitchTimes = new Queue<long>();

        int m_LastStrokeCount;
        float m_LastModeState;
        bool m_LastModeStateInitialized;

        // 帧计数器
        int m_FrameCount;

        // 构建非 Viseme 的 52 维索引映射
        int[] m_NonVisemeIndices;
        int[] m_ReverseMap; // 72维 -> 在52维中的索引

        #endregion

        void Awake()
        {
            if (m_DataCollector == null)
                m_DataCollector = FindAnyObjectByType<MultiChannelDataCollector>();
        }

        void Start()
        {
            InitializeFaceProcessing();
            InitializeLowpassFilter();
            InitializeHandHistoryBuffers();
            m_FrameCount = 0;
        }

        #region 初始化

        void InitializeFaceProcessing()
        {
            // 构建 52 维非 Viseme 索引映射
            var nonVisemeSet = new HashSet<int>(m_VisemeIndices);
            var nonVisemeList = new List<int>();
            m_ReverseMap = new int[72];

            for (int i = 0; i < 72; i++)
            {
                if (!nonVisemeSet.Contains(i))
                {
                    m_ReverseMap[i] = nonVisemeList.Count;
                    nonVisemeList.Add(i);
                }
                else
                {
                    m_ReverseMap[i] = -1;
                }
            }

            m_NonVisemeIndices = nonVisemeList.ToArray();
            m_FaceRunningMean = new float[52];
            m_FaceRunningVar = new float[52];
            m_FaceUpdateCount = 0;

            Debug.Log($"[MECSense] Face: {m_NonVisemeIndices.Length} non-viseme dimensions, {m_VisemeIndices.Length} viseme removed");
        }

        void InitializeLowpassFilter()
        {
            // 二阶巴特沃斯低通滤波器，截止频率 4Hz @90Hz 采样
            float fs = m_RenderFPS;
            float fc = m_LowpassCutoff;
            float omega = 2f * Mathf.PI * fc / fs;
            float cosOmega = Mathf.Cos(omega);
            float sinOmega = Mathf.Sin(omega);
            float alpha = sinOmega / (2f * Mathf.Sqrt(2f));

            float a0 = 1f + alpha;
            m_LowpassB = new float[] { (1f - cosOmega) / (2f * a0), (1f - cosOmega) / a0, (1f - cosOmega) / (2f * a0) };
            m_LowpassA = new float[] { 1f, -2f * cosOmega / a0, (1f - alpha) / a0 };

            // 为每根指尖 × 每轴分配4维状态 [x_n-1, x_n-2, y_n-1, y_n-2]
            m_FilterState = new float[3][][];
            for (int i = 0; i < 3; i++)
            {
                m_FilterState[i] = new float[3][];
                for (int axis = 0; axis < 3; axis++)
                    m_FilterState[i][axis] = new float[4]; // [x[n-1], x[n-2], y[n-1], y[n-2]]
            }
        }

        void InitializeHandHistoryBuffers()
        {
            for (int i = 0; i < 3; i++)
            {
                m_HandHistory[i] = new float[k_HandWindowSize];
            }
        }

        #endregion

        void Update()
        {
            if (m_DataCollector == null || !m_DataCollector.IsDataValid)
                return;

            m_FrameCount++;

            // 每帧更新各通道的滑动窗口缓冲
            UpdateFaceBuffer();
            UpdateEyeBuffer();
            UpdateHandBuffer();

            // 每 N 帧输出一次特征
            if (m_FrameCount % m_InferenceIntervalFrames == 0)
            {
                ExtractFeatureVector();
                m_FeatureReady = true;
            }
        }

        #region 面部特征（52维）

        void UpdateFaceBuffer()
        {
            var bs = m_DataCollector.FaceBlendShapes;
            if (bs == null || bs.Length < 72)
                return;

            // 提取 52 维非 Viseme 特征
            var frame52 = new float[52];
            for (int i = 0; i < m_NonVisemeIndices.Length; i++)
            {
                frame52[i] = bs[m_NonVisemeIndices[i]];
            }

            m_FaceWindow.Enqueue(frame52);
            if (m_FaceWindow.Count > k_FaceWindowSize)
                m_FaceWindow.Dequeue();

            // 更新运行均值和方差
            m_FaceUpdateCount++;
            float n = m_FaceUpdateCount;
            for (int i = 0; i < 52; i++)
            {
                float delta = frame52[i] - m_FaceRunningMean[i];
                m_FaceRunningMean[i] += delta / n;
                float delta2 = frame52[i] - m_FaceRunningMean[i];
                m_FaceRunningVar[i] += (delta * delta2) / n;
            }
        }

        /// <summary>
        /// 计算 52 维面部特征：时域均值 + Z-score 标准化
        /// </summary>
        float[] ExtractFaceFeatures()
        {
            // 1. 1秒滑动窗口内时域均值
            var windowMean = new float[52];
            int count = m_FaceWindow.Count;
            if (count == 0) return windowMean;

            foreach (var frame in m_FaceWindow)
            {
                for (int i = 0; i < 52; i++)
                    windowMean[i] += frame[i];
            }
            for (int i = 0; i < 52; i++)
                windowMean[i] /= count;

            // 2. Z-score 标准化（使用运行均值和方差）
            var result = new float[52];
            for (int i = 0; i < 52; i++)
            {
                float std = Mathf.Sqrt(Mathf.Max(m_FaceRunningVar[i], 1e-8f));
                result[i] = (windowMean[i] - m_FaceRunningMean[i]) / std;
            }

            return result;
        }

        #endregion

        #region 眼动特征（3维）

        void UpdateEyeBuffer()
        {
            // 注视方向
            m_GazeWindow.Enqueue(m_DataCollector.GazeDirection);
            if (m_GazeWindow.Count > k_GazeWindowSize)
                m_GazeWindow.Dequeue();

            // 瞳孔直径
            m_PupilHistory.Enqueue(m_DataCollector.PupilDiameter);
            if (m_PupilHistory.Count > k_GazeWindowSize + 1)
                m_PupilHistory.Dequeue();

            // 眨眼事件
            m_BlinkWindow.Enqueue((m_FrameCount, m_DataCollector.BlinkEvent));
            if (m_BlinkWindow.Count > k_BlinkWindowSize)
                m_BlinkWindow.Dequeue();
        }

        float[] ExtractEyeFeatures()
        {
            var result = new float[3];

            // 1. 注视发散度 σ_gaze
            if (m_GazeWindow.Count > 1)
            {
                Vector3 mean = Vector3.zero;
                foreach (var g in m_GazeWindow)
                    mean += g;
                mean /= m_GazeWindow.Count;

                float variance = 0;
                foreach (var g in m_GazeWindow)
                {
                    var diff = g - mean;
                    variance += diff.sqrMagnitude;
                }
                variance /= m_GazeWindow.Count;
                result[0] = Mathf.Sqrt(variance);
            }

            // 2. 瞳孔直径变化率 r_pupil
            if (m_PupilHistory.Count > 1)
            {
                var arr = m_PupilHistory.ToArray();
                float sumDiff = 0;
                for (int i = 1; i < arr.Length; i++)
                    sumDiff += Mathf.Abs(arr[i] - arr[i - 1]);
                result[1] = sumDiff / (arr.Length - 1);
            }

            // 3. 瞬时眨眼频率 f_blink (归一化为每分钟)
            int blinkCount = 0;
            foreach (var (_, blink) in m_BlinkWindow)
            {
                if (blink) blinkCount++;
            }
            float windowSeconds = m_BlinkWindow.Count / m_RenderFPS;
            result[2] = windowSeconds > 0 ? (blinkCount / windowSeconds) * 60f : 0f;

            if (m_EnableDebugLog)
            {
                Debug.Log($"[MECSense] Eye: σ_gaze={result[0]:F4}, r_pupil={result[1]:F4}, f_blink={result[2]:F1}/min");
            }

            return result;
        }

        #endregion

        #region 手部微颤（1维）

        void UpdateHandBuffer()
        {
            var joints = m_DataCollector.HandJoints;
            if (joints == null || joints.Length < 21)
                return;

            // 更新3根指尖
            for (int i = 0; i < 3; i++)
            {
                int jointIdx = k_FingertipJoints[i];
                Vector3 pos = joints[jointIdx];

                // 对 xyz 分别进行低通滤波
                float[] filtered = new float[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    float input = axis == 0 ? pos.x : (axis == 1 ? pos.y : pos.z);
                    filtered[axis] = ApplyLowpassFilter(i, axis, input);
                }

                m_FilteredPositions[i] = new Vector3(filtered[0], filtered[1], filtered[2]);

                // 高频分量 = 原始 - 低频
                Vector3 highFreq = pos - m_FilteredPositions[i];

                // 存入历史缓冲
                int idx = m_HandHistoryCount % k_HandWindowSize;
                m_HandHistory[i][idx] = highFreq.magnitude;
            }

            m_HandHistoryCount++;
        }

        float ApplyLowpassFilter(int finger, int axis, float input)
        {
            // 状态: s[0]=x[n-1], s[1]=x[n-2], s[2]=y[n-1], s[3]=y[n-2]
            float[] s = m_FilterState[finger][axis];

            // 二阶 IIR DF-II (Direct Form II Transposed):
            // y[n] = b0*x[n] + s[0]
            // s[0] = b1*x[n] - a1*y[n] + s[1]
            // s[1] = b2*x[n] - a2*y[n]
            float yn = m_LowpassB[0] * input + s[0];
            s[0] = m_LowpassB[1] * input - m_LowpassA[1] * yn + s[1];
            s[1] = m_LowpassB[2] * input - m_LowpassA[2] * yn;

            return yn;
        }

        float ExtractHandJitter()
        {
            int N = Mathf.Min(m_HandHistoryCount, k_HandWindowSize);
            if (N < 2) return 0f;

            // 环形缓冲区：计算最旧样本的起始偏移
            int startOffset = (m_HandHistoryCount - N + k_HandWindowSize) % k_HandWindowSize;

            float sum = 0f;
            for (int i = 0; i < 3; i++) // 3根指尖
            {
                for (int j = 1; j < N; j++)
                {
                    int idxCurr = (startOffset + j) % k_HandWindowSize;
                    int idxPrev = (startOffset + j - 1) % k_HandWindowSize;
                    float diff = m_HandHistory[i][idxCurr] - m_HandHistory[i][idxPrev];
                    sum += diff * diff;
                }
            }

            float fs = m_RenderFPS;
            float jitter = Mathf.Sqrt((sum / (3f * (N - 1))) * fs * fs);
            // 转换为 mm/s (假设 Unity 单位 = 米)
            m_JitterRate = jitter * 1000f; // m/s -> mm/s

            if (m_EnableDebugLog)
            {
                Debug.Log($"[MECSense] Hand Jitter: {m_JitterRate:F2} mm/s (N={N}, startOffset={startOffset})");
            }

            return m_JitterRate;
        }

        float m_JitterRate;

        #endregion

        #region 交互行为特征（3维）

        float[] ExtractInteractionFeatures()
        {
            var result = new float[3];
            long nowFrame = m_FrameCount;

            // 1. 笔触间平均停顿时长（10秒窗口）
            CleanupWindow(m_StrokeEndTimes, k_StrokeWindow, nowFrame);
            if (m_StrokeEndTimes.Count >= 2)
            {
                var times = m_StrokeEndTimes.ToArray();
                float totalPause = 0;
                for (int i = 1; i < times.Length; i++)
                    totalPause += (times[i] - times[i - 1]) / m_RenderFPS;
                result[0] = totalPause / (times.Length - 1);
            }

            // 2. 单位时间撤销次数（30秒窗口）
            CleanupWindow(m_UndoTimes, k_UndoWindow, nowFrame);
            result[1] = m_UndoTimes.Count > 0 ? m_UndoTimes.Count / (k_UndoWindow / m_RenderFPS) : 0f;

            // 3. 模式切换频率（60秒窗口）
            CleanupWindow(m_ModeSwitchTimes, k_ModeSwitchWindow, nowFrame);
            result[2] = m_ModeSwitchTimes.Count > 0 ? m_ModeSwitchTimes.Count / (k_ModeSwitchWindow / m_RenderFPS) : 0f;

            return result;
        }

        void CleanupWindow(Queue<long> queue, int windowSize, long nowFrame)
        {
            while (queue.Count > 0 && (nowFrame - queue.Peek()) > windowSize)
                queue.Dequeue();
        }

        #endregion

        #region 事件接口（供绘画系统调用）

        /// <summary>
        /// 推送交互行为事件
        /// </summary>
        public void PushInteractionEvent(InteractionEvent evt)
        {
            long frame = m_FrameCount;

            switch (evt.Type)
            {
                case InteractionEventType.StrokeEnd:
                    m_StrokeEndTimes.Enqueue(frame);
                    break;
                case InteractionEventType.Undo:
                    m_UndoTimes.Enqueue(frame);
                    break;
                case InteractionEventType.ModeSwitch:
                    m_ModeSwitchTimes.Enqueue(frame);
                    break;
            }

            if (m_EnableDebugLog)
            {
                Debug.Log($"[MECSense] Event: {evt.Type} @ frame {frame}");
            }
        }

        /// <summary>
        /// 便捷方法：推送笔触结束事件
        /// </summary>
        public void NotifyStrokeEnd() => PushInteractionEvent(new InteractionEvent { Type = InteractionEventType.StrokeEnd, Timestamp = m_FrameCount });

        /// <summary>
        /// 便捷方法：推送撤销事件
        /// </summary>
        public void NotifyUndo() => PushInteractionEvent(new InteractionEvent { Type = InteractionEventType.Undo, Timestamp = m_FrameCount });

        /// <summary>
        /// 便捷方法：推送模式切换事件
        /// </summary>
        public void NotifyModeSwitch() => PushInteractionEvent(new InteractionEvent { Type = InteractionEventType.ModeSwitch, Timestamp = m_FrameCount });

        #endregion

        #region 特征向量拼接

        void ExtractFeatureVector()
        {
            // [52 面部] + [3 眼动] + [1 手部微颤] + [3 交互行为] = 59
            var face = ExtractFaceFeatures();
            var eye = ExtractEyeFeatures();
            float hand = ExtractHandJitter();
            var interaction = ExtractInteractionFeatures();

            int offset = 0;
            for (int i = 0; i < 52; i++) m_FeatureVector[offset++] = face[i];
            for (int i = 0; i < 3; i++)  m_FeatureVector[offset++] = eye[i];
            m_FeatureVector[offset++] = hand;
            for (int i = 0; i < 3; i++)  m_FeatureVector[offset++] = interaction[i];

            if (m_EnableDebugLog)
            {
                Debug.Log($"[MECSense] Feature vector extracted: 59 dims, face_range=[{face[0]:F3}..{face[51]:F3}], jitter={hand:F1}mm/s");
            }
        }

        #endregion
    }
}
