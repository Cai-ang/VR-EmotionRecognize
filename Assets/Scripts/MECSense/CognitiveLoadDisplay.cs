using UnityEngine;
using TMPro;

namespace MECSense
{
    /// <summary>
    /// 认知负荷状态UI显示器。
    /// 监听 CognitiveLoadEstimator 的推理结果，将负荷等级、分数、通道偏离度
    /// 实时显示在挂载的 TextMeshPro 组件上。
    /// 
    /// 使用方式：挂载到任意 GameObject，将 StateText 引用到 stateCanvas 下的 Text (TMP)。
    /// 也可留空，自动按路径查找 "stateCanvas/Text (TMP)"。
    /// </summary>
    public class CognitiveLoadDisplay : MonoBehaviour
    {
        [Header("UI Reference")]
        [SerializeField]
        [Tooltip("显示状态的 TextMeshPro 组件（stateCanvas/Text (TMP)）")]
        TMP_Text m_StateText;

        [Header("Data Source")]
        [SerializeField]
        [Tooltip("认知负荷估算器")]
        CognitiveLoadEstimator m_Estimator;

        [Header("Display Options")]
        [SerializeField]
        [Tooltip("是否显示各通道偏离度详情")]
        bool m_ShowChannelDetails = true;

        [SerializeField]
        [Tooltip("文本刷新间隔（秒）")]
        float m_RefreshInterval = 0.33f; // 与推理周期对齐

        [SerializeField]
        [Tooltip("Low等级颜色")]
        Color m_LowColor = new Color(0.2f, 0.8f, 0.3f, 1f);   // 绿色

        [SerializeField]
        [Tooltip("Medium等级颜色")]
        Color m_MediumColor = new Color(1f, 0.8f, 0.1f, 1f);   // 黄色

        [SerializeField]
        [Tooltip("High等级颜色")]
        Color m_HighColor = new Color(1f, 0.3f, 0.2f, 1f);     // 红色

        float m_LastRefreshTime;
        CognitiveLoadEstimator.CognitiveLoadResult m_LastResult;

        // 通道名称（用于显示）
        static readonly string[] k_ChannelNames = {
            "面部", "注视", "瞳孔", "眨眼", "微颤", "停顿", "纠错"
        };

        void Awake()
        {
            // 自动发现引用
            if (m_StateText == null)
                m_StateText = FindStateText();
            if (m_Estimator == null)
                m_Estimator = FindAnyObjectByType<CognitiveLoadEstimator>();

            // 订阅事件
            if (m_Estimator != null)
            {
                var field = typeof(CognitiveLoadEstimator).GetField("m_OnLoadUpdated",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    var evt = field.GetValue(m_Estimator) as UnityEngine.Events.UnityEvent<CognitiveLoadEstimator.CognitiveLoadResult>;
                    if (evt != null)
                        evt.AddListener(OnLoadUpdated);
                }
            }
        }

        void Update()
        {
            // 无事件驱动时降级为轮询
            if (m_Estimator == null || m_StateText == null)
                return;

            if (Time.time - m_LastRefreshTime < m_RefreshInterval)
                return;

            m_LastRefreshTime = Time.time;

            // 如果事件没触发过，手动取最新结果
            if (m_LastResult.Timestamp <= 0)
                m_LastResult = m_Estimator.CurrentResult;

            RefreshDisplay();
        }

        /// <summary>事件回调</summary>
        void OnLoadUpdated(CognitiveLoadEstimator.CognitiveLoadResult result)
        {
            m_LastResult = result;
            RefreshDisplay();
        }

        void RefreshDisplay()
        {
            if (m_StateText == null) return;

            var r = m_LastResult;

            // 基线未校准时
            if (!r.BaselineAvailable)
            {
                m_StateText.text = "<color=#AAAAAA>等待基线校准...</color>";
                m_StateText.color = Color.gray;
                return;
            }

            // 根据等级选颜色
            Color levelColor = r.Level switch
            {
                CognitiveLoadLevel.Low => m_LowColor,
                CognitiveLoadLevel.Medium => m_MediumColor,
                CognitiveLoadLevel.High => m_HighColor,
                _ => Color.white
            };
            m_StateText.color = levelColor;

            string levelStr = r.Level switch
            {
                CognitiveLoadLevel.Low => "低",
                CognitiveLoadLevel.Medium => "中",
                CognitiveLoadLevel.High => "高",
                _ => "?"
            };

            // 构建显示文本
            var sb = new System.Text.StringBuilder(256);
            sb.AppendLine($"<b>认知负荷: {levelStr}</b>");
            sb.AppendLine($"得分: {r.SmoothedScore:F2} (原始 {r.RawScore:F2})");

            if (m_ShowChannelDetails && r.ChannelDeviations != null && r.ChannelDeviations.Length >= 7)
            {
                sb.AppendLine();
                sb.AppendLine("<size=24><i>通道偏离度:</i></size>");
                for (int i = 0; i < 7; i++)
                {
                    // 用颜色标记高偏离通道
                    float dev = r.ChannelDeviations[i];
                    string colorTag = dev > 1.5f ? "#FF6B4A" : (dev > 0.8f ? "#FFD93D" : "#FFFFFF");
                    sb.AppendLine($"  {k_ChannelNames[i]}: <color={colorTag}>{dev:F2}</color>");
                }
            }

            m_StateText.text = sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 按路径自动查找 stateCanvas 下的 Text (TMP)
        /// </summary>
        TMP_Text FindStateText()
        {
            // 尝试路径查找
            var canvas = GameObject.Find("stateCanvas");
            if (canvas != null)
            {
                var text = canvas.GetComponentInChildren<TMP_Text>();
                if (text != null)
                {
                    Debug.Log("[MECSense] CognitiveLoadDisplay 自动找到 stateCanvas/Text(TMP)");
                    return text;
                }
            }

            Debug.LogWarning("[MECSense] CognitiveLoadDisplay 未找到 stateCanvas 下的 Text(TMP)");
            return null;
        }

        void OnDestroy()
        {
            // 取消订阅
            if (m_Estimator != null)
            {
                try
                {
                    var field = typeof(CognitiveLoadEstimator).GetField("m_OnLoadUpdated",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null)
                    {
                        var evt = field.GetValue(m_Estimator) as UnityEngine.Events.UnityEvent<CognitiveLoadEstimator.CognitiveLoadResult>;
                        evt?.RemoveListener(OnLoadUpdated);
                    }
                }
                catch { }
            }
        }
    }
}
