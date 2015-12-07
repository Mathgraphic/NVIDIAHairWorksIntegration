using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif


[AddComponentMenu("Hair Works Integration/Hair Instance")]
[ExecuteInEditMode]
public class HairInstance : MonoBehaviour
{
    #region static
    static HashSet<HairInstance> s_instances;
    static int s_nth_LateUpdate;
    static int s_nth_OnWillRenderObject;

    static CommandBuffer s_command_buffer;
    static HashSet<Camera> s_cameras = new HashSet<Camera>();

    static public HashSet<HairInstance> GetInstances()
    {
        if (s_instances == null)
        {
            s_instances = new HashSet<HairInstance>();
        }
        return s_instances;
    }
    #endregion

    static CameraEvent s_timing = CameraEvent.BeforeImageEffects;
    public string m_hair_asset;
    public string m_hair_shader = "HairWorksIntegration/DefaultHairShader.cso";
    public Transform m_root_bone;
    public bool m_invert_bone_x = true;
    public hwDescriptor m_params = hwDescriptor.default_value;
	public Texture2D root;
	public Texture2D tip;
	public Texture2D specular;
    hwHShader m_hshader = hwHShader.NullHandle;
    hwHAsset m_hasset = hwHAsset.NullHandle;
    hwHInstance m_hinstance = hwHInstance.NullHandle;

    public Transform[] m_bones;
    Matrix4x4[] m_inv_bindpose;
    Matrix4x4[] m_skinning_matrices;
    IntPtr m_skinning_matrices_ptr;
    Matrix4x4 m_conversion_matrix;

    public Mesh m_probe_mesh;


    public uint shader_id { get { return m_hshader; } }
    public uint asset_id { get { return m_hasset; } }
    public uint instance_id { get { return m_hinstance; } }
	
	  
	static Vector4[] avCoeff     = new Vector4[7];

	public bool updateTextures = true;

    void RepaintWindow()
    {
#if UNITY_EDITOR
        var assembly = typeof(UnityEditor.EditorWindow).Assembly;
        var type = assembly.GetType("UnityEditor.GameView");
        var gameview = EditorWindow.GetWindow(type);
        gameview.Repaint();
#endif
    }

    public void LoadHairShader(string path_to_cso)
    {
        // release existing shader
        if (m_hshader)
        {
            HairWorksIntegration.hwShaderRelease(m_hshader);
            m_hshader = hwHShader.NullHandle;
        }

        // load shader
        if (m_hshader = HairWorksIntegration.hwShaderLoadFromFile(Application.streamingAssetsPath + "/" + path_to_cso))
        {
            m_hair_shader = path_to_cso;
        }
#if UNITY_EDITOR
        RepaintWindow();
#endif
    }

    public void ReloadHairShader()
    {
        HairWorksIntegration.hwShaderReload(m_hshader);
        RepaintWindow();
    }

    public void LoadHairAsset(string path_to_apx, bool reset_params=true)
    {
        // release existing instance & asset
        if (m_hinstance)
        {
            HairWorksIntegration.hwInstanceRelease(m_hinstance);
            m_hinstance = hwHInstance.NullHandle;
        }
        if (m_hasset)
        {
            HairWorksIntegration.hwAssetRelease(m_hasset);
            m_hasset = hwHAsset.NullHandle;
        }

        // load & create instance
        if (m_hasset = HairWorksIntegration.hwAssetLoadFromFile(Application.streamingAssetsPath + "/" + path_to_apx))
        {
            m_hair_asset = path_to_apx;
            m_hinstance = HairWorksIntegration.hwInstanceCreate(m_hasset);
            if(reset_params)
            {
                HairWorksIntegration.hwAssetGetDefaultDescriptor(m_hasset, ref m_params);
            }
        }

        // update bone structure
        if(reset_params)
        {
            m_bones = null;
            m_skinning_matrices = null;
            m_skinning_matrices_ptr = IntPtr.Zero;
        }
        UpdateBones();

#if UNITY_EDITOR
        Update();
        RepaintWindow();
#endif
    }


    public void ReloadHairAsset()
    {
        HairWorksIntegration.hwAssetReload(m_hasset);
        HairWorksIntegration.hwAssetGetDefaultDescriptor(m_hasset, ref m_params);
        HairWorksIntegration.hwInstanceSetDescriptor(m_hinstance, ref m_params);
        RepaintWindow();
    }

    public void AssignTexture(hwTextureType type, Texture2D tex)
    {
        HairWorksIntegration.hwInstanceSetTexture(m_hinstance, type, tex.GetNativeTexturePtr());
		print (tex.format);
    }

    public void UpdateBones()
    {
        int num_bones = HairWorksIntegration.hwAssetGetNumBones(m_hasset);
        if (m_bones == null || m_bones.Length != num_bones)
        {
            m_bones = new Transform[num_bones];

            if (m_root_bone == null)
            {
                m_root_bone = GetComponent<Transform>();
            }

            var children = m_root_bone.GetComponentsInChildren<Transform>();
            for (int i = 0; i < num_bones; ++i)
            {
                string name = HairWorksIntegration.hwAssetGetBoneNameString(m_hasset, i);
                m_bones[i] = Array.Find(children, (a) => { return a.name == name; });
                if (m_bones[i] == null) { m_bones[i] = m_root_bone; }
            }

        }

        if(m_skinning_matrices == null)
        {
            m_inv_bindpose = new Matrix4x4[num_bones];
            m_skinning_matrices = new Matrix4x4[num_bones];
            m_skinning_matrices_ptr = Marshal.UnsafeAddrOfPinnedArrayElement(m_skinning_matrices, 0);
            for (int i = 0; i < num_bones; ++i)
            {
                m_inv_bindpose[i] = Matrix4x4.identity;
                m_skinning_matrices[i] = Matrix4x4.identity;
            }

            for (int i = 0; i < num_bones; ++i)
            {
                HairWorksIntegration.hwAssetGetBindPose(m_hasset, i, ref m_inv_bindpose[i]);
                m_inv_bindpose[i] = m_inv_bindpose[i].inverse;
            }

            m_conversion_matrix = Matrix4x4.identity;
            if (m_invert_bone_x)
            {
                m_conversion_matrix *= Matrix4x4.Scale(new Vector3(-1.0f, 1.0f, 1.0f));
            }
        }


        for (int i = 0; i < m_bones.Length; ++i)
        {
            var t = m_bones[i];
            if (t != null)
            {
                m_skinning_matrices[i] = t.localToWorldMatrix * m_conversion_matrix * m_inv_bindpose[i];
            }
        }
    }

    static public void Swap<T>(ref T a, ref T b)
    {
        T tmp = a;
        a = b;
        b = tmp;
    }

	public void UpdateTextures(Texture2D Root, Texture2D Tip, Texture2D Specular){
		if (root)
			this.root = Root;
		if (tip)
			this.tip = Tip;
		if (Specular)
			this.specular = Specular;
		this.updateTextures = true;
	}

#if UNITY_EDITOR
    void Reset()
    {
        var skinned_mesh_renderer = GetComponent<SkinnedMeshRenderer>();
        m_root_bone = skinned_mesh_renderer!=null ? skinned_mesh_renderer.rootBone : GetComponent<Transform>();

        var renderer = GetComponent<Renderer>();
        if(renderer == null)
        {
            m_probe_mesh = new Mesh();
            m_probe_mesh.name = "Probe";
            m_probe_mesh.vertices = new Vector3[1] { Vector3.zero };
            m_probe_mesh.SetIndices(new int[1] { 0 }, MeshTopology.Points, 0);

            var mesh_filter = gameObject.AddComponent<MeshFilter>();
            mesh_filter.sharedMesh = m_probe_mesh;
            renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Material[0] { };
        }
    }
#endif

    void OnApplicationQuit()
    {
        ClearCommandBuffer();
    }

    void Awake()
    {
		HairWorksIntegration.hwSetLogCallback();
	}
	
	void OnDestroy()
    {
        HairWorksIntegration.hwInstanceRelease(m_hinstance);
        HairWorksIntegration.hwAssetRelease(m_hasset);
    }

    void OnEnable()
    {
        GetInstances().Add(this);
        m_params.m_enable = true;
    }

    void OnDisable()
    {
        m_params.m_enable = false;
        GetInstances().Remove(this);
    }

    void Start()
    {
        LoadHairShader(m_hair_shader);
        LoadHairAsset(m_hair_asset, false);
		updateTextures = true;
		
		
    }

    void Update()
    {
        UpdateBones();
        HairWorksIntegration.hwInstanceSetDescriptor(m_hinstance, ref m_params);
        HairWorksIntegration.hwInstanceUpdateSkinningMatrices(m_hinstance, m_skinning_matrices.Length, m_skinning_matrices_ptr);

        if (m_probe_mesh != null)
        {
            var bmin = Vector3.zero;
            var bmax = Vector3.zero;
            HairWorksIntegration.hwInstanceGetBounds(m_hinstance, ref bmin, ref bmax);

            var center = (bmin + bmax) * 0.5f;
            var size = bmax - center;
            m_probe_mesh.bounds = new Bounds(center, size);
        }
        if (updateTextures)
        {
            if (root != null)
               AssignTexture(hwTextureType.ROOT_COLOR, root);
            if (tip != null)
                AssignTexture(hwTextureType.TIP_COLOR, tip);
            if (specular != null)
                AssignTexture(hwTextureType.SPECULAR, specular);
            updateTextures = false;
        }

        s_nth_LateUpdate = 0;
    }


    void LateUpdate()
    {

       
			HairWorksIntegration.hwStepSimulation (Time.deltaTime);
		SphericalHarmonicsL2 aSample;    // SH sample consists of 27 floats   
		LightProbes.GetInterpolatedProbe(this.transform.position, this.GetComponent<MeshRenderer>(), out aSample);
			for (int iC=0; iC<3; iC++) {
				avCoeff[iC] = new Vector4((float)aSample [iC, 3], aSample [iC, 1], aSample [iC, 2], aSample [iC, 0] - aSample [iC, 6]);
			}
			for (int iC=0; iC<3; iC++) {
				avCoeff [iC + 3].x = aSample [iC, 4];
				avCoeff [iC + 3].y = aSample [iC, 5];
				avCoeff [iC + 3].z = 3.0f * aSample [iC, 6];
				avCoeff [iC + 3].w = aSample [iC, 7];
			}
			avCoeff [6].x = aSample [0, 8];
			avCoeff [6].y = aSample [1, 8];
			avCoeff [6].y = aSample [2, 8];
			avCoeff [6].w = 1.0f;


		
		
	}
	
	void OnWillRenderObject()
    {

		if (s_nth_OnWillRenderObject++ == 0) {

			BeginRender ();
			foreach (var a in GetInstances()) {
				a.Render ();
			}
			EndRender ();
		}
    }

    void OnRenderObject()
    {
        s_nth_OnWillRenderObject = 0;
    }




    static public bool IsDeferred(Camera cam)
    {
        if (cam.renderingPath == RenderingPath.DeferredShading
#if UNITY_EDITOR
            || (cam.renderingPath == RenderingPath.UsePlayerSettings && PlayerSettings.renderingPath == RenderingPath.DeferredShading)
#endif
            )
        {
            return true;
        }
        return false;
    }

    static public bool DoesRenderToTexture(Camera cam)
    {
        return IsDeferred(cam) || cam.targetTexture != null;
    }



    static public void ClearCommandBuffer()
    {
        foreach (var c in s_cameras)
        {
            if (c != null)
            {
                c.RemoveCommandBuffer(s_timing, s_command_buffer);
            }
        }
        s_cameras.Clear();
    }

    static void BeginRender()
    {
        if (s_command_buffer == null)
        {
            s_command_buffer = new CommandBuffer();
            s_command_buffer.name = "Hair";
            s_command_buffer.IssuePluginEvent(HairWorksIntegration.hwGetRenderEventFunc(), 0);
        }
		var cam = Camera.current;
		bool goodToGo = false;
		#if UNITY_EDITOR
		if (UnityEditor.EditorApplication.isPlaying)
			goodToGo = cam.CompareTag("MainCamera");
		else goodToGo = cam.CompareTag("Untagged");
		#else
		goodToGo = cam.CompareTag("MainCamera");
		#endif
        if(cam != null && goodToGo)
        {

            Matrix4x4 V = cam.worldToCameraMatrix;
            Matrix4x4 P = GL.GetGPUProjectionMatrix(cam.projectionMatrix, DoesRenderToTexture(cam));
            float fov = cam.fieldOfView;
            HairWorksIntegration.hwSetViewProjection(ref V, ref P, fov);
			HairLight.AssignLightData();   

            if (!s_cameras.Contains(cam))
            {
                cam.AddCommandBuffer(s_timing, s_command_buffer);
                s_cameras.Add(cam);
            }

        }

        HairWorksIntegration.hwBeginScene();
    }

	public static void BeginRenderVR(Camera cam){
		if (s_command_buffer == null) {
			s_command_buffer = new CommandBuffer ();
			s_command_buffer.name = "Hair";
			s_command_buffer.IssuePluginEvent (HairWorksIntegration.hwGetRenderEventFunc (), 0);
		}

		Matrix4x4 V = cam.worldToCameraMatrix;
		Matrix4x4 P = GL.GetGPUProjectionMatrix (cam.projectionMatrix, DoesRenderToTexture (cam));
		float fov = cam.fieldOfView;
		HairWorksIntegration.hwSetViewProjection (ref V, ref P, fov);
		HairLight.AssignLightData ();
		if (!s_cameras.Contains (cam)) {
			cam.AddCommandBuffer (s_timing, s_command_buffer);
			s_cameras.Add (cam);
		}
		HairWorksIntegration.hwBeginScene();
	}


   public void Render()
    {
       
		if (!m_hasset) { return; }
        HairWorksIntegration.hwSetShader(m_hshader);
		HairWorksIntegration.hwSetSphericalHarmonics(ref avCoeff [0], ref avCoeff [1], ref avCoeff [2], ref avCoeff [3], ref avCoeff [4], ref avCoeff [5], ref avCoeff [6]);
        HairWorksIntegration.hwRender(m_hinstance);
    }

    public static void EndRender()
    {
        HairWorksIntegration.hwEndScene();
    }

}
