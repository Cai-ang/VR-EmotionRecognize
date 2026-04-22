using UnityEngine;

namespace MECSense
{
    /// <summary>
    /// 交互事件桥接器：将绘画系统的行为事件（笔触结束等）桥接到 FeatureExtractor。
    /// 挂载在场景任意 GameObject 上即可，Awake 时自动发现并连接两端。
    /// </summary>
    public class InteractionBridge : MonoBehaviour
    {
        [Header("References (可选，留空则自动查找)")]
        [SerializeField]
        [Tooltip("绘画检测器（PenDrawingDetector 组件）")]
        PenDrawingDetector m_PenDrawingDetector;

        [SerializeField]
        [Tooltip("特征提取器")]
        FeatureExtractor m_FeatureExtractor;

        bool m_Wired;

        void Awake()
        {
            if (m_PenDrawingDetector == null)
                m_PenDrawingDetector = FindAnyObjectByType<PenDrawingDetector>();

            if (m_FeatureExtractor == null)
                m_FeatureExtractor = FindAnyObjectByType<FeatureExtractor>();
        }

        void OnEnable()
        {
            WireEvents();
        }

        void OnDisable()
        {
            UnwireEvents();
        }

        void WireEvents()
        {
            if (m_Wired) return;

            if (m_PenDrawingDetector != null && m_FeatureExtractor != null)
            {
                // 笔触结束 → StrokeEnd 事件
                m_PenDrawingDetector.OnDrawingStateChanged += OnDrawingStateChanged;
                m_Wired = true;
                Debug.Log("[MECSense] InteractionBridge 已连接: PenDrawingDetector -> FeatureExtractor");
            }
            else
            {
                Debug.LogWarning($"[MECSense] InteractionBridge 连接失败: " +
                    $"Pen={(m_PenDrawingDetector != null ? "OK" : "NULL")} " +
                    $"FE={(m_FeatureExtractor != null ? "OK" : "NULL")}");
            }
        }

        void UnwireEvents()
        {
            if (!m_Wired) return;
            if (m_PenDrawingDetector != null)
                m_PenDrawingDetector.OnDrawingStateChanged -= OnDrawingStateChanged;
            m_Wired = false;
        }

        /// <summary>
        /// 监听绘画状态变化：从绘制→停止时触发 StrokeEnd
        /// </summary>
        void OnDrawingStateChanged(bool isDrawing)
        {
            if (!isDrawing && m_FeatureExtractor != null)
            {
                // 绘画结束时通知特征提取器
                m_FeatureExtractor.NotifyStrokeEnd();

                if (m_FeatureExtractor.HandJitterRate > 0f)
                {
                    Debug.Log($"[MECSense] StrokeEnd @ jitter={m_FeatureExtractor.HandJitterRate:F1}mm/s");
                }
            }
        }
    }
}
