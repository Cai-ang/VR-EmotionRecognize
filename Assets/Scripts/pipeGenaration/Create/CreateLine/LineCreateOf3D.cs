using System.Collections.Generic;
using UnityEngine;
namespace pipegenaration
{
    struct PipePoint
    {
        Vector3 location;
        Vector3 direction;

        public Vector3 Location { get => location; set => location = value; }
        public Vector3 Direction { get => direction; set => direction = value; }
    }
    public class LineCreateOf3D
    {
        public GameObject CreateLine(GameObject mesh, Vector3[] createPoint, int circularCount, float circularR, float elbowR)
        {
            MeshFilter filter = mesh.GetComponent<MeshFilter>();
            Vector3[] circul = CircularSection(circularCount, circularR);
            List<PipePoint> pipePoints = SetPipePoint(createPoint, elbowR);
            Vector3[] meshPoint = CreateMeshPoint(pipePoints, circul);
            filter.mesh = CreatMesh(pipePoints, meshPoint, circul);
            return mesh;
        }

        #region 横切圆创建
        /// <summary>
        /// 得到管线横切圆
        /// </summary>
        /// <param name="Count">段数</param>
        /// <param name="R">半径</param>
        /// <returns></returns>
        Vector3[] CircularSection(int Count, float R)
        {
            Vector3[] vector3s = new Vector3[Count];
            float angle = 360 / Count;
            Vector3 vector3 = new Vector3(R, 0, 0);
            for (int i = 0; i < Count; i++)
            {
                //根据角度得到圆的分布点
                vector3s[i] = vector3.ToAngle(angle * i, Vector3.zero, Vector3.forward);
            }
            return vector3s;
        }
        #endregion

        #region 得到3d线的中心路径
        /// <summary>
        /// 设置中心路径
        /// </summary>
        /// <param name="createPoint"></param>
        /// <returns></returns>
        List<PipePoint> SetPipePoint(Vector3[] createPoint, float elbowR)
        {
            List<PipePoint> pipePoints = new List<PipePoint>();
            int length = createPoint.Length;
            for (int i = 0; i < length; i++)
            {
                if (i == 0)
                {
                    AddPipePoints(createPoint[i], createPoint[i + 1] - createPoint[i], ref pipePoints);
                }
                else if (i == length - 1)
                {
                    AddPipePoints(createPoint[i], createPoint[i] - createPoint[i - 1], ref pipePoints);
                }
                else
                {
                    GetElbowPoint(createPoint[i], createPoint[i - 1], createPoint[i + 1], elbowR, ref pipePoints);
                }
            }
            return pipePoints;
        }
        /// <summary>
        /// 增加管线点
        /// </summary>
        /// <param name="location"></param>
        /// <param name="direction"></param>
        void AddPipePoints(Vector3 location, Vector3 direction, ref List<PipePoint> pipePoints)
        {
            PipePoint pipePoint = new PipePoint();
            pipePoint.Location = location;
            pipePoint.Direction = direction;
            pipePoints.Add(pipePoint);
        }
        /// <summary>
        /// 得到弯头点
        /// </summary>
        /// <param name="focus"></param>
        /// <param name="front"></param>
        /// <param name="back"></param>
        /// <param name="r">内切圆半径</param>
        void GetElbowPoint(Vector3 focus, Vector3 front, Vector3 back, float r, ref List<PipePoint> pipePoints)
        {
            //焦点前后向量
            Vector3 frontVec = focus - front;
            Vector3 backVec = back - focus;
            //得到前后切点
            Vector3 tangencyFront = ((frontVec.magnitude - r) * frontVec.normalized) + front;
            Vector3 tangencyBack = (r * backVec.normalized) + focus;
            //得到内切圆圆心
            Vector3 circulPoint = GetCirculPoint(focus, tangencyFront, tangencyBack);
            //得到弯头分段
            Vector3[] circulSection = GetCirculSection(focus, tangencyFront, tangencyBack, circulPoint);
            //得到两个焦点向量的法线
            Vector3 normal = Vector3.Cross(frontVec, backVec).normalized;
            //增加管线点
            AddPipePoints(tangencyFront, GetDirection(tangencyFront, circulPoint, normal), ref pipePoints);
            int length = circulSection.Length;
            for (int i = 0; i < length; i++)
            {
                AddPipePoints(circulSection[i], GetDirection(circulSection[i], circulPoint, normal), ref pipePoints);
            }
            AddPipePoints(tangencyBack, GetDirection(tangencyBack, circulPoint, normal), ref pipePoints);
        }
        /// <summary>
        /// 得到弯点在内切圆上的切线方向
        /// </summary>
        /// <param name="self"></param>
        /// <param name="circulPoint"></param>
        /// <param name="normal"></param>
        /// <returns></returns>
        Vector3 GetDirection(Vector3 self, Vector3 circulPoint, Vector3 normal)
        {
            Vector3 vector = circulPoint - self;
            return Vector3.Cross(vector, normal).normalized;
        }
        /// <summary>
        /// 得到管线弯头内切圆分段点
        /// </summary>
        /// <param name="focus">交点</param>
        /// <param name="tangency1">切点1</param>
        /// <param name="tangency2">切点2</param>
        /// <param name="circulPoint">切圆圆心</param>
        /// <returns></returns>
        Vector3[] GetCirculSection(Vector3 focus, Vector3 tangency1, Vector3 tangency2, Vector3 circulPoint)
        {
            Vector3 vector0 = tangency1 - circulPoint;
            Vector3 vector4 = tangency2 - circulPoint;
            float dis = vector0.magnitude;
            Vector3 vector2 = (vector0 + vector4).normalized * dis;
            Vector3 vector1 = (vector0 + vector2).normalized * dis;
            Vector3 vector3 = (vector4 + vector2).normalized * dis;
            Vector3[] vector3s = new Vector3[3];
            vector3s[0] = vector1 + circulPoint;
            vector3s[1] = vector2 + circulPoint;
            vector3s[2] = vector3 + circulPoint;
            return vector3s;
        }
        /// <summary>
        /// 得到管线弯头内切圆圆心
        /// </summary>
        /// <param name="focus">交点</param>
        /// <param name="tangency1">切点1</param>
        /// <param name="tangency2">切点2</param>
        /// <returns></returns>
        Vector3 GetCirculPoint(Vector3 focus, Vector3 tangency1, Vector3 tangency2)
        {
            Vector3 vector1 = tangency1 - focus;
            Vector3 vector2 = tangency2 - focus;
            Vector3 vector0 = (vector1 + vector2).normalized;
            float angle = Vector3.Angle(vector1, vector0);
            float dis = vector1.magnitude / Mathf.Cos(angle * Mathf.Deg2Rad);
            return (vector0 * dis) + focus;
        }
        #endregion

        #region 创建网格点
        Vector3[] CreateMeshPoint(List<PipePoint> pipePoint, Vector3[] circular)
        {
            int length = pipePoint.Count;
            int circularCount = circular.Length;
            Vector3[] meshPoint = new Vector3[length * circularCount];
            for (int i = 0; i < length; i++)
            {
                for (int j = 0; j < circularCount; j++)
                {
                    meshPoint[(i * circularCount) + j] = circular[j].FromToMoveRotation(pipePoint[i].Location, pipePoint[i].Direction);
                }
            }
            return meshPoint;
        }
        #endregion

        #region 网格创建
        Mesh CreatMesh(List<PipePoint> pipePoints, Vector3[] meshPoint, Vector3[] circular)
        {
            Mesh mesh = new Mesh();
            int circularCount = circular.Length;
            mesh.vertices = meshPoint;
            mesh.triangles = GetTriangles(pipePoints, circularCount);
            mesh.uv = GetUV(pipePoints, circular);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
        Vector2[] GetUV(List<PipePoint> pipePoints, Vector3[] circular)
        {
            int length = pipePoints.Count;
            int circularCount = circular.Length;
            Vector2[] uvs = new Vector2[(circularCount * length)];
            float lineDis = 0;
            float circularDis = Vector3.Distance(circular[0], circular[1]);
            int k = 0;
            for (int i = 0; i < length; i++)
            {
                if (i != 0)
                {
                    lineDis += Vector3.Distance(pipePoints[i].Location, pipePoints[i - 1].Location);
                }
                for (int j = 0; j < circularCount; j++)
                {
                    Vector2 vector2;
                    if (j % 2 != 0)
                    {
                        vector2 = new Vector2(circularDis, lineDis);
                    }
                    else
                    {
                        vector2 = new Vector2(0, lineDis);
                    }

                    uvs[k] = vector2;
                    k += 1;
                }
            }
            return uvs;
        }
        int[] GetTriangles(List<PipePoint> pipePoints, int Count)
        {
            int length = pipePoints.Count;
            int[] triangles = new int[(Count * (length - 1)) * 6];
            int k = 0;
            for (int i = 0; i < length - 1; i++)
            {
                for (int j = 0; j < Count; j++)
                {
                    if (j == Count - 1)
                    {
                        triangles[k] = (i * Count) + j;
                        triangles[k + 1] = (i * Count) + 0;
                        triangles[k + 2] = ((i + 1) * Count) + 0;
                        triangles[k + 3] = (i * Count) + j;
                        triangles[k + 4] = ((i + 1) * Count) + 0;
                        triangles[k + 5] = ((i + 1) * Count) + j;
                    }
                    else
                    {
                        triangles[k] = (i * Count) + j;
                        triangles[k + 1] = (i * Count) + j + 1;
                        triangles[k + 2] = ((i + 1) * Count) + j + 1;
                        triangles[k + 3] = (i * Count) + j;
                        triangles[k + 4] = ((i + 1) * Count) + j + 1;
                        triangles[k + 5] = ((i + 1) * Count) + j;
                    }
                    k += 6;
                }
            }
            return triangles;
        }
        #endregion
    }
}
