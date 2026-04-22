using System.Collections.Generic;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 挂载在 VR 摄像机的子物体 Canvas 上，获取 PICO 面部追踪数据，
/// 调用 EmotionRecognizer 推理情绪，并在 VR 视野左上角显示结果。
/// </summary>
public class EmotionDisplay : MonoBehaviour
{
    [Header("Model")]
    [SerializeField]
    [Tooltip("EmotionRecognizer 组件（需手动拖入）")]
    EmotionRecognizer m_EmotionRecognizer;

    [Header("UI")]
    [SerializeField]
    [Tooltip("情绪标签文本")]
    TMP_Text m_EmotionLabel;
    [SerializeField]
    [Tooltip("置信度文本")]
    TMP_Text m_ConfidenceText;
    [SerializeField]
    [Tooltip("详情文本（显示所有概率）")]
    TMP_Text m_DetailText;

    [Header("Settings")]
    [SerializeField]
    [Tooltip("推理间隔（每 N 帧推理一次，0=每帧）")]
    int m_InferenceInterval = 5;

    [SerializeField]
    [Tooltip("情绪标签对应的中文名")]
    string[] m_EmotionChineseNames = {
        "高兴", "悲伤", "愤怒", "惊讶", "恐惧", "厌恶", "中性"
    };

    // 面部追踪
    PxrFaceTrackingInfo m_FaceTrackingInfo;
    float[] m_BlendShapeWeight = new float[72];
    bool m_FaceTrackingReady;

    // 推理状态
    int m_FrameCount;
    string m_LastEmotion = "等待中...";
    float m_LastConfidence;
    float[] m_LastProbabilities;
    EmotionPrediction m_LastPrediction;

    // 时序平滑 - 多数投票
    Queue<string> m_EmotionHistory = new Queue<string>();
    const int k_HistorySize = 10;

    void Start()
    {
        InitFaceTracking();
        InitUI();
    }

    void InitFaceTracking()
    {
        if (!PXR_Plugin.System.UPxr_QueryDeviceAbilities(PxrDeviceAbilities.PxrTrackingModeFaceBit))
        {
            Debug.LogError("[EmotionDisplay] 设备不支持面部追踪！");
            return;
        }

        PXR_MotionTracking.WantFaceTrackingService();
        FaceTrackingStartInfo info = new FaceTrackingStartInfo();
        info.mode = FaceTrackingMode.PXR_FTM_FACE_LIPS_BS;
        PXR_MotionTracking.StartFaceTracking(ref info);
        m_FaceTrackingInfo = new PxrFaceTrackingInfo();
        m_FaceTrackingReady = true;
        Debug.Log("[EmotionDisplay] 面部追踪已启动");
    }

    void InitUI()
    {
        if (m_EmotionLabel != null)
            m_EmotionLabel.text = "情绪: 检测中...";
        if (m_ConfidenceText != null)
            m_ConfidenceText.text = "置信度: --%";
        if (m_DetailText != null)
            m_DetailText.text = "";
    }

    void Update()
    {
        m_FrameCount++;

        // 获取面部追踪数据
        if (!m_FaceTrackingReady)
            return;

        switch (PXR_Manager.Instance.trackingMode)
        {
            case FaceTrackingMode.PXR_FTM_FACE_LIPS_BS:
                PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_FACELIP_DATA, ref m_FaceTrackingInfo);
                break;
            case FaceTrackingMode.PXR_FTM_FACE:
                PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_FACE_DATA, ref m_FaceTrackingInfo);
                break;
            case FaceTrackingMode.PXR_FTM_LIPS:
                PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_LIP_DATA, ref m_FaceTrackingInfo);
                break;
        }

        // 读取 72 维 blendShape
        unsafe
        {
            fixed (float* source = m_FaceTrackingInfo.blendShapeWeight)
            {
                for (int i = 0; i < 72; i++)
                    m_BlendShapeWeight[i] = source[i];
            }
        }

        // 推理
        if (m_InferenceInterval <= 0 || m_FrameCount % m_InferenceInterval == 0)
        {
            RunInference();
        }
    }

    void RunInference()
    {
        if (m_EmotionRecognizer == null)
            return;

        try
        {
            m_LastPrediction = m_EmotionRecognizer.PredictEmotionWithProbabilities(m_BlendShapeWeight);
            if (m_LastPrediction == null || m_LastPrediction.probabilities == null)
                return;

            // 多数投票平滑
            m_EmotionHistory.Enqueue(m_LastPrediction.predictedEmotion);
            if (m_EmotionHistory.Count > k_HistorySize)
                m_EmotionHistory.Dequeue();

            string votedEmotion = MajorityVote();

            // 找到投票后的置信度
            int votedIdx = -1;
            float votedConf = 0;
            for (int i = 0; i < m_LastPrediction.classNames.Length; i++)
            {
                if (m_LastPrediction.classNames[i] == votedEmotion)
                {
                    votedIdx = i;
                    votedConf = m_LastPrediction.probabilities[i];
                    break;
                }
            }

            m_LastEmotion = TranslateToChinese(votedEmotion);
            m_LastConfidence = votedConf;
            m_LastProbabilities = m_LastPrediction.probabilities;

            UpdateUI();
        }
        catch (System.Exception e)
        {
            if (m_FrameCount % 60 == 0)
                Debug.LogError($"[EmotionDisplay] 推理失败: {e.Message}");
        }
    }

    string MajorityVote()
    {
        var counts = new Dictionary<string, int>();
        foreach (var e in m_EmotionHistory)
        {
            if (!counts.ContainsKey(e)) counts[e] = 0;
            counts[e]++;
        }

        string best = "";
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

    string TranslateToChinese(string english)
    {
        // 根据 EmotionRecognizer 的 classNames 映射
        if (m_LastPrediction != null && m_LastPrediction.classNames != null
            && m_LastPrediction.probabilities != null)
        {
            for (int i = 0; i < m_LastPrediction.classNames.Length; i++)
            {
                if (m_LastPrediction.classNames[i] == english && i < m_EmotionChineseNames.Length)
                    return m_EmotionChineseNames[i];
            }
        }
        return english;
    }

    void UpdateUI()
    {
        if (m_EmotionLabel != null)
            m_EmotionLabel.text = $"情绪: {m_LastEmotion}";

        if (m_ConfidenceText != null)
            m_ConfidenceText.text = $"置信度: {m_LastConfidence * 100:F1}%";

        if (m_DetailText != null && m_LastPrediction != null && m_LastPrediction.classNames != null)
        {
            string detail = "";
            for (int i = 0; i < m_LastPrediction.classNames.Length; i++)
            {
                string name = i < m_EmotionChineseNames.Length
                    ? m_EmotionChineseNames[i]
                    : m_LastPrediction.classNames[i];
                float prob = m_LastPrediction.probabilities[i];
                detail += $"{name}: {prob * 100:F1}%\n";
            }
            m_DetailText.text = detail;
        }
    }
}
