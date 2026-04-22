using System.Collections.Generic;
using System.IO;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class EmotionDataRecorder : MonoBehaviour
{
    [Header("UI References")]
    public Button recordButton;
    public TMP_Text recordTimeText;
    public TMP_Text statusText;

    [Header("Recording Settings")]
    public float recordDuration = 10f; // 记录时长（秒）
    public int sampleRate = 30; // 采样频率（Hz，每秒采样次数）
    public string emotionLabel = "neutral"; // 当前表情标签（手动设置）

    // 面部追踪数据
    private PxrFaceTrackingInfo faceTrackingInfo;
    private float[] blendShapeWeight = new float[72];

    // ARKit标准blendShape名称列表（共72个）
    private string[] blendShapeNames = new string[]
    {
        "eyeLookDownLeft",
        "noseSneerLeft",
        "eyeLookInLeft",
        "browInnerUp",
        "browDownRight",
        "mouthClose",
        "mouthLowerDownRight",
        "jawOpen",
        "mouthUpperUpRight",
        "mouthShrugUpper",
        "mouthFunnel",
        "eyeLookInRight",
        "eyeLookDownRight",
        "noseSneerRight",
        "mouthRollUpper",
        "jawRight",
        "browDownLeft",
        "mouthShrugLower",
        "mouthRollLower",
        "mouthSmileLeft",
        "mouthPressLeft",
        "mouthSmileRight",
        "mouthPressRight",
        "mouthDimpleRight",
        "mouthLeft",
        "jawForward",
        "eyeSquintLeft",
        "mouthFrownLeft",
        "eyeBlinkLeft",
        "cheekSquintLeft",
        "browOuterUpLeft",
        "eyeLookUpLeft",
        "jawLeft",
        "mouthStretchLeft",
        "mouthPucker",
        "eyeLookUpRight",
        "browOuterUpRight",
        "cheekSquintRight",
        "eyeBlinkRight",
        "mouthUpperUpLeft",
        "mouthFrownRight",
        "eyeSquintRight",
        "mouthStretchRight",
        "cheekPuff",
        "eyeLookOutLeft",
        "eyeLookOutRight",
        "eyeWideRight",
        "eyeWideLeft",
        "mouthRight",
        "mouthDimpleLeft",
        "mouthLowerDownLeft",
        "tongueOut",
        "viseme_PP",
        "viseme_CH",
        "viseme_o",
        "viseme_O",
        "viseme_i",
        "viseme_I",
        "viseme_RR",
        "viseme_XX",
        "viseme_aa",
        "viseme_FF",
        "viseme_u",
        "viseme_U",
        "viseme_TH",
        "viseme_kk",
        "viseme_SS",
        "viseme_e",
        "viseme_DD",
        "viseme_E",
        "viseme_nn",
        "viseme_sil"
    };

    // 记录状态
    private bool isRecording = false;
    private float recordTimer = 0f;
    private float sampleTimer = 0f;
    private List<EmotionData> recordedData = new List<EmotionData>();

    // 数据保存路径
    private string savePath;

    [System.Serializable]
    private class EmotionData
    {
        public float timestamp; // 时间戳（相对于记录开始的时间，秒）
        public float[] blendShapeWeights = new float[72]; // 72个blendShape权重值
    }

    void Start()
    {
        // 初始化按钮事件
        if (recordButton != null)
        {
            recordButton.onClick.AddListener(ToggleRecording);
        }

        // 初始化UI文本
        if (recordTimeText != null)
        {
            recordTimeText.text = "00.00s";
        }
        if (statusText != null)
        {
            statusText.text = "Ready to record";
            statusText.color = Color.white;
        }

        // 设置保存路径
        savePath = Path.Combine(Application.persistentDataPath, "EmotionData");
        if (!Directory.Exists(savePath))
        {
            Directory.CreateDirectory(savePath);
        }

        // 初始化面部追踪
        Debug.Log("EmotionDataRecorder: Initializing face tracking...");

        if (!PXR_Plugin.System.UPxr_QueryDeviceAbilities(PxrDeviceAbilities.PxrTrackingModeFaceBit))
        {
            Debug.LogError("EmotionDataRecorder: Device does not support face tracking!");
            if (statusText != null)
            {
                statusText.text = "Error: Face tracking not supported";
                statusText.color = Color.red;
            }
            return;
        }

        PXR_MotionTracking.WantFaceTrackingService();
        FaceTrackingStartInfo info = new FaceTrackingStartInfo();
        info.mode = FaceTrackingMode.PXR_FTM_FACE_LIPS_BS;
        PXR_MotionTracking.StartFaceTracking(ref info);
        Debug.Log("EmotionDataRecorder: Face tracking started with mode: " + info.mode);
    }

    void Update()
    {
        // 更新记录状态
        if (isRecording)
        {
            // 更新计时器
            recordTimer += Time.deltaTime;
            sampleTimer += Time.deltaTime;

            // 更新UI显示
            if (recordTimeText != null)
            {
                recordTimeText.text = recordTimer.ToString("00.00") + "s";
            }

            // 采样数据
            float sampleInterval = 1f / sampleRate;
            if (sampleTimer >= sampleInterval)
            {
                RecordEmotionData();
                sampleTimer = 0f;
            }

            // 检查是否达到记录时长
            if (recordTimer >= recordDuration)
            {
                StopRecording();
            }
        }
    }

    /// <summary>
    /// 切换记录状态（开始/停止）
    /// </summary>
    public void ToggleRecording()
    {
        if (isRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    /// <summary>
    /// 开始记录
    /// </summary>
    public void StartRecording()
    {
        recordedData.Clear();
        recordTimer = 0f;
        sampleTimer = 0f;
        isRecording = true;

        // 更新UI
        if (recordButton != null)
        {
            var buttonText = recordButton.GetComponentInChildren<TMP_Text>();
            if (buttonText != null)
            {
                buttonText.text = "停止记录";
            }
            recordButton.image.color = Color.red;
        }
        if (statusText != null)
        {
            statusText.text = $"Recording: {emotionLabel} ({recordDuration}s)";
            statusText.color = Color.green;
        }
        if (recordTimeText != null)
        {
            recordTimeText.text = "00.00s";
        }

        Debug.Log($"EmotionDataRecorder: Started recording emotion: {emotionLabel}");
    }

    /// <summary>
    /// 停止记录并保存数据
    /// </summary>
    public void StopRecording()
    {
        isRecording = false;

        // 更新UI
        if (recordButton != null)
        {
            var buttonText = recordButton.GetComponentInChildren<TMP_Text>();
            if (buttonText != null)
            {
                buttonText.text = "开始记录";
            }
            recordButton.image.color = Color.white;
        }
        if (statusText != null)
        {
            statusText.text = $"Saved {recordedData.Count} samples";
            statusText.color = Color.yellow;
        }

        // 保存数据
        if (recordedData.Count > 0)
        {
            SaveRecordedData();
        }

        Debug.Log($"EmotionDataRecorder: Stopped recording. Total samples: {recordedData.Count}");
    }

    /// <summary>
    /// 记录当前帧的表情数据
    /// </summary>
    private void RecordEmotionData()
    {
        // 获取面部追踪数据
        PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_FACELIP_DATA, ref faceTrackingInfo);

        // 创建新的数据记录
        EmotionData data = new EmotionData();
        data.timestamp = recordTimer;

        // 复制blendShape权重（使用unsafe指针）
        unsafe
        {
            fixed (float* source = faceTrackingInfo.blendShapeWeight)
            {
                for (int i = 0; i < 72; i++)
                {
                    data.blendShapeWeights[i] = source[i];
                }
            }
        }

        // 添加到记录列表
        recordedData.Add(data);
    }

    /// <summary>
    /// 保存记录的数据到CSV文件
    /// </summary>
    private void SaveRecordedData()
    {
        // 生成文件名：emotion_标签_时间戳.csv
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"emotion_{emotionLabel}_{timestamp}.csv";
        string filePath = Path.Combine(savePath, fileName);

        // 写入CSV文件
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            // 写入表头（使用ARKit标准blendShape名称）
            writer.Write("timestamp");
            for (int i = 0; i < 72; i++)
            {
                writer.Write($",{blendShapeNames[i]}");
            }
            writer.WriteLine();

            // 写入数据
            foreach (EmotionData data in recordedData)
            {
                writer.Write(data.timestamp.ToString("0.000"));
                for (int i = 0; i < 72; i++)
                {
                    writer.Write($",{data.blendShapeWeights[i]:F4}");
                }
                writer.WriteLine();
            }
        }

        Debug.Log($"EmotionDataRecorder: Data saved to {filePath}");

        // 也可以保存为JSON格式，方便后续加载
        string jsonFileName = $"emotion_{emotionLabel}_{timestamp}.json";
        string jsonFilePath = Path.Combine(savePath, jsonFileName);
        string jsonData = JsonUtility.ToJson(new EmotionDataContainer
        {
            emotionLabel = emotionLabel,
            recordDuration = recordDuration,
            sampleRate = sampleRate,
            sampleCount = recordedData.Count,
            blendShapeNames = blendShapeNames,
            data = recordedData
        }, true);
        File.WriteAllText(jsonFilePath, jsonData);
        Debug.Log($"EmotionDataRecorder: JSON data saved to {jsonFilePath}");
    }

    /// <summary>
    /// 设置表情标签（用于标记当前记录的表情类型）
    /// </summary>
    public void SetEmotionLabel(string label)
    {
        emotionLabel = label;
        Debug.Log($"EmotionDataRecorder: Emotion label set to: {label}");
    }

    /// <summary>
    /// 获取数据保存路径
    /// </summary>
    public string GetSavePath()
    {
        return savePath;
    }

    void OnDisable()
    {
        // 如果正在记录，停止并保存
        if (isRecording)
        {
            StopRecording();
        }
    }

    // 用于JSON序列化的容器类
    [System.Serializable]
    private class EmotionDataContainer
    {
        public string emotionLabel;
        public float recordDuration;
        public int sampleRate;
        public int sampleCount;
        public string[] blendShapeNames;
        public List<EmotionData> data;
    }
}
