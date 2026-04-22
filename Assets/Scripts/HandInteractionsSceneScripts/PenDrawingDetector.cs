using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using pipegenaration;

/// <summary>
/// 挂载在 Pen_3D 上，检测是否正在绘画，并在 tip 端生成 3D 管状笔画。
/// 判定条件：中指、无名指、小拇指的 fullCurl 均大于阈值，且 Pen_3D 被食指和拇指 pinch 握住。
/// </summary>
public class PenDrawingDetector : MonoBehaviour
{
    [Header("Hand Detection")]
    [SerializeField]
    [Tooltip("检测哪只手")]
    Handedness m_Handedness = Handedness.Right;

    [SerializeField]
    [Tooltip("手指弯曲判定阈值 (0~1，fullCurl 大于此值视为弯曲)")]
    [Range(0f, 1f)]
    float m_CurlThreshold = 0.7f;

    [SerializeField]
    [Tooltip("XRGrabInteractable 组件（通常在同一个 GameObject 上）")]
    XRGrabInteractable m_GrabInteractable;

    [Header("UI")]
    [SerializeField]
    [Tooltip("用于显示状态的 TextMeshProUGUI 组件（留空则自动查找 Dialog/Text (TMP)）")]
    TextMeshProUGUI m_StatusText;

    [Header("Drawing")]
    [SerializeField]
    [Tooltip("笔尖子物体")]
    Transform m_Tip;

    [SerializeField]
    [Tooltip("笔画材质")]
    Material m_DrawingMaterial;

    [SerializeField]
    [Tooltip("笔画颜色")]
    Color m_DrawColor = Color.white;

    [SerializeField]
    [Tooltip("笔画采样最小间距")]
    float m_DrawInterval = 0.02f;

    [SerializeField]
    [Tooltip("管道截面段数")]
    int m_Segments = 6;

    [SerializeField]
    [Tooltip("管道半径")]
    float m_Radius = 2f;

    [SerializeField]
    [Tooltip("弯头半径")]
    float m_ElbowRadius = 4f;

    const string k_StatusTextPath = "Dialog/Text (TMP)";
    const string k_DrawingText = "Drawing";
    const string k_IdleText = "Idle";

    /// <summary>
    /// 当前是否正在绘画
    /// </summary>
    public bool IsDrawing { get; private set; }

    /// <summary>
    /// 绘画状态变化时触发，参数为当前是否正在绘画
    /// </summary>
    public event System.Action<bool> OnDrawingStateChanged;

    static List<XRHandSubsystem> s_SubsystemsReuse = new List<XRHandSubsystem>();

    /// <summary>
    /// 获取中指、无名指、小拇指的 fullCurl 值（供外部读取调试）
    /// </summary>
    public void GetFingerCurls(out float middleCurl, out float ringCurl, out float littleCurl)
    {
        middleCurl = m_MiddleCurl;
        ringCurl = m_RingCurl;
        littleCurl = m_LittleCurl;
    }

    float m_MiddleCurl;
    float m_RingCurl;
    float m_LittleCurl;

    LineCreateOf3D m_LineCreator;
    GameObject m_CurrentPipeMesh;
    Mesh m_CurrentMesh;
    List<Vector3> m_PipePoints = new List<Vector3>();
    GameObject m_VisualLine;

    void Awake()
    {
        if (m_GrabInteractable == null)
            m_GrabInteractable = GetComponent<XRGrabInteractable>();

        if (m_GrabInteractable == null)
            Debug.LogError($"PenDrawingDetector: 未找到 XRGrabInteractable 组件，请手动指定。", this);

        if (m_StatusText == null)
            m_StatusText = transform.Find(k_StatusTextPath)?.GetComponent<TextMeshProUGUI>();

        if (m_StatusText != null)
            m_StatusText.text = k_IdleText;

        if (m_Tip == null)
            m_Tip = transform.Find("tip");

        if (m_Tip == null)
            Debug.LogError($"PenDrawingDetector: 未找到 tip 子物体，无法绘画。", this);

        m_LineCreator = new LineCreateOf3D();
    }

    void Update()
    {
        if (m_GrabInteractable == null)
            return;

        if (m_StatusText == null)
            m_StatusText = transform.Find(k_StatusTextPath)?.GetComponent<TextMeshProUGUI>();

        var subsystem = TryGetSubsystem();
        if (subsystem == null)
        {
            SetDrawingState(false);
            return;
        }

        var hand = m_Handedness == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;

        var middleShape = hand.CalculateFingerShape(XRHandFingerID.Middle, XRFingerShapeTypes.FullCurl);
        var ringShape = hand.CalculateFingerShape(XRHandFingerID.Ring, XRFingerShapeTypes.FullCurl);
        var littleShape = hand.CalculateFingerShape(XRHandFingerID.Little, XRFingerShapeTypes.FullCurl);

        middleShape.TryGetFullCurl(out m_MiddleCurl);
        ringShape.TryGetFullCurl(out m_RingCurl);
        littleShape.TryGetFullCurl(out m_LittleCurl);

        bool fingersCurled = m_MiddleCurl > m_CurlThreshold
                          && m_RingCurl > m_CurlThreshold
                          && m_LittleCurl > m_CurlThreshold;

        bool isGrabbed = m_GrabInteractable.isSelected;

        Debug.Log($"[PenDrawing] isGrabbed={isGrabbed} | Middle={m_MiddleCurl:F3} Ring={m_RingCurl:F3} Little={m_LittleCurl:F3} | IsDrawing={fingersCurled && isGrabbed}");

        SetDrawingState(fingersCurled && isGrabbed);

        if (IsDrawing && m_Tip != null)
            DrawPipe();
    }

    void DrawPipe()
    {
        if (m_DrawingMaterial == null)
        {
            Debug.LogWarning("PenDrawingDetector: 未指定笔画材质，跳过绘画。");
            return;
        }

        if (m_CurrentPipeMesh == null)
        {
            m_CurrentPipeMesh = new GameObject("PipeMesh");
            MeshFilter mf = m_CurrentPipeMesh.AddComponent<MeshFilter>();
            MeshRenderer mr = m_CurrentPipeMesh.AddComponent<MeshRenderer>();
            mr.material = m_DrawingMaterial;
            mr.material.color = m_DrawColor;
            m_CurrentMesh = new Mesh();
            mf.mesh = m_CurrentMesh;

            m_PipePoints.Clear();
            m_PipePoints.Add(m_Tip.position);
        }
        else
        {
            var currentPos = m_PipePoints[m_PipePoints.Count - 1];
            if (Vector3.Distance(currentPos, m_Tip.position) > m_DrawInterval)
            {
                m_PipePoints.Add(m_Tip.position);
                Vector3[] points = m_PipePoints.ToArray();
                m_CurrentPipeMesh = m_LineCreator.CreateLine(m_CurrentPipeMesh, points, m_Segments, m_Radius, m_ElbowRadius);

                // 创建可视化线条
                Destroy(m_VisualLine);
                m_VisualLine = new GameObject("visualline");
                MeshFilter vf = m_VisualLine.AddComponent<MeshFilter>();
                MeshRenderer vr = m_VisualLine.AddComponent<MeshRenderer>();
                vr.material = m_DrawingMaterial;
                vr.material.color = m_DrawColor;
                Mesh vm = new Mesh();
                vf.mesh = vm;

                // 设置半透明
                Renderer renderer = m_VisualLine.GetComponent<Renderer>();
                Material material = renderer.material;
                material.SetFloat("_Mode", 2);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = 3000;
                Color color = material.color;
                color.a = 0.5f;
                material.color = color;
            }
        }
    }

    void SetDrawingState(bool drawing)
    {
        if (IsDrawing == drawing)
            return;

        if (!drawing && IsDrawing)
            EndStroke();

        IsDrawing = drawing;
        if (m_StatusText != null)
            m_StatusText.text = drawing ? k_DrawingText : k_IdleText;
        OnDrawingStateChanged?.Invoke(IsDrawing);
    }

    void EndStroke()
    {
        Destroy(m_VisualLine);
        m_VisualLine = null;
        m_CurrentPipeMesh = null;
        m_CurrentMesh = null;
        m_PipePoints.Clear();
    }

    static XRHandSubsystem TryGetSubsystem()
    {
        SubsystemManager.GetSubsystems(s_SubsystemsReuse);
        return s_SubsystemsReuse.Count > 0 ? s_SubsystemsReuse[0] : null;
    }
}
