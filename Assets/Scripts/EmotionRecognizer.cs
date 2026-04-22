using UnityEngine;
using Unity.Barracuda;
using System.IO;
using System;

public class EmotionRecognizer : MonoBehaviour
{
    [Header("Model Settings")]
    public NNModel modelAsset;
    public TextAsset metadataAsset; // JSON格式的元数据文件

    [Header("UI References")]
    public TMPro.TMP_Text resultText;
    public TMPro.TMP_Text confidenceText;

    private Model runtimeModel;
    private IWorker worker;

    // 标准化参数
    private float[] scalerMean;
    private float[] scalerStd;
    private string[] classNames;
    private int numFeatures;
    private int numClasses;

    private bool isInitialized = false;

    void Start()
    {
        InitializeModel();
    }

    /// <summary>
    /// 初始化模型和参数
    /// </summary>
    public void InitializeModel()
    {
        if (modelAsset == null)
        {
            Debug.LogError("EmotionRecognizer: Model asset is not assigned!");
            return;
        }

        if (metadataAsset == null)
        {
            Debug.LogError("EmotionRecognizer: Metadata asset is not assigned!");
            return;
        }

        try
        {
            // 加载模型
            runtimeModel = ModelLoader.Load(modelAsset);
            worker = WorkerFactory.CreateWorker(WorkerFactory.Type.Compute, runtimeModel);

            // 加载元数据
            LoadMetadata();

            isInitialized = true;
            Debug.Log("EmotionRecognizer: Model initialized successfully!");
        }
        catch (Exception e)
        {
            Debug.LogError($"EmotionRecognizer: Initialization failed - {e.Message}");
        }
    }

    /// <summary>
    /// 从JSON加载模型元数据
    /// </summary>
    private void LoadMetadata()
    {
        try
        {
            string json = metadataAsset.text;
            var metadata = JsonUtility.FromJson<ModelMetadata>(json);

            scalerMean = new float[metadata.num_features];
            scalerStd = new float[metadata.num_features];
            classNames = new string[metadata.num_classes];
            numFeatures = metadata.num_features;
            numClasses = metadata.num_classes;

            // 解析JSON数组
            string[] meanStr = metadata.scaler_mean_json.Split(',');
            string[] stdStr = metadata.scaler_std_json.Split(',');

            for (int i = 0; i < numFeatures; i++)
            {
                scalerMean[i] = float.Parse(meanStr[i].Trim('[', ']'));
                scalerStd[i] = float.Parse(stdStr[i].Trim('[', ']'));
            }

            string[] classStr = metadata.class_names_json.Trim('[', ']').Split(',');
            for (int i = 0; i < numClasses; i++)
            {
                classNames[i] = classStr[i].Trim('"');
            }

            Debug.Log($"EmotionRecognizer: Loaded {numClasses} classes with {numFeatures} features");
        }
        catch (Exception e)
        {
            Debug.LogError($"EmotionRecognizer: Failed to load metadata - {e.Message}");
        }
    }

    /// <summary>
    /// 预测表情（输入blendShape权重数组）
    /// </summary>
    /// <param name="blendShapeWeights">72个blendShape权重</param>
    /// <returns>预测的表情类别</returns>
    public string PredictEmotion(float[] blendShapeWeights)
    {
        if (!isInitialized || worker == null)
        {
            Debug.LogWarning("EmotionRecognizer: Model not initialized!");
            return "Unknown";
        }

        if (blendShapeWeights == null || blendShapeWeights.Length != 72)
        {
            Debug.LogError($"EmotionRecognizer: Expected 72 features, got {blendShapeWeights?.Length ?? 0}");
            return "Unknown";
        }

        try
        {
            // 1. 准备输入（去除最后15个viseme特征）
            float[] inputFeatures = new float[numFeatures];
            for (int i = 0; i < numFeatures; i++)
            {
                inputFeatures[i] = blendShapeWeights[i];
            }

            // 2. 标准化输入
            float[] normalizedInput = new float[numFeatures];
            for (int i = 0; i < numFeatures; i++)
            {
                normalizedInput[i] = (inputFeatures[i] - scalerMean[i]) / scalerStd[i];
            }

            // 3. 创建输入张量
            using Tensor inputTensor = new Tensor(1, numFeatures, normalizedInput);

            // 4. 执行推理
            worker.Execute(inputTensor);

            // 5. 获取输出
            using Tensor outputTensor = worker.PeekOutput();
            float[] output = outputTensor.data.Download(outputTensor.shape);

            // 6. 找到最大概率的类别
            int predictedClass = 0;
            float maxProb = output[0];
            for (int i = 1; i < output.Length; i++)
            {
                if (output[i] > maxProb)
                {
                    maxProb = output[i];
                    predictedClass = i;
                }
            }

            // 7. 更新UI
            if (resultText != null)
            {
                resultText.text = $"表情: {classNames[predictedClass]}";
            }
            if (confidenceText != null)
            {
                confidenceText.text = $"置信度: {maxProb * 100:F1}%";
            }

            // 8. 返回结果
            return classNames[predictedClass];
        }
        catch (Exception e)
        {
            Debug.LogError($"EmotionRecognizer: Prediction failed - {e.Message}");
            return "Unknown";
        }
    }

    /// <summary>
    /// 预测表情并返回所有类别的概率
    /// </summary>
    public EmotionPrediction PredictEmotionWithProbabilities(float[] blendShapeWeights)
    {
        string emotion = PredictEmotion(blendShapeWeights);

        // 获取所有概率（需要重新推理，因为上面的方法已经dispose了tensor）
        float[] features = new float[numFeatures];
        for (int i = 0; i < numFeatures; i++)
        {
            features[i] = blendShapeWeights[i];
        }

        float[] normalized = new float[numFeatures];
        for (int i = 0; i < numFeatures; i++)
        {
            normalized[i] = (features[i] - scalerMean[i]) / scalerStd[i];
        }

        using Tensor inputTensor = new Tensor(1, numFeatures, normalized);
        worker.Execute(inputTensor);
        using Tensor outputTensor = worker.PeekOutput();
        float[] output = outputTensor.data.Download(outputTensor.shape);

        EmotionPrediction prediction = new EmotionPrediction();
        prediction.predictedEmotion = emotion;
        prediction.probabilities = new float[numClasses];
        prediction.classNames = new string[numClasses];

        for (int i = 0; i < numClasses; i++)
        {
            prediction.probabilities[i] = output[i];
            prediction.classNames[i] = classNames[i];
        }

        return prediction;
    }

    void OnDestroy()
    {
        worker?.Dispose();
    }

    // 元数据结构
    [Serializable]
    private class ModelMetadata
    {
        public int num_features;
        public int num_classes;
        public string scaler_mean_json; // JSON数组字符串
        public string scaler_std_json;
        public string class_names_json;
    }
}

// 预测结果结构
[Serializable]
public class EmotionPrediction
{
    public string predictedEmotion;
    public float[] probabilities;
    public string[] classNames;
}
