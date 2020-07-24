using QuickJS;
using QuickJS.Binding;
using QuickJS.Utils;
using QuickJS.IO;

namespace jsb
{
    using UnityEngine;

    public class Sample : MonoBehaviour, IScriptRuntimeListener
    {
        public enum FileLoader
        {
            Default,
            Resources,
            HMR,
        }
        public FileLoader fileLoader;
        public string baseUrl = "http://127.0.0.1:8182";
        public bool sourceMap;
        public bool stacktrace;
        private ScriptRuntime _rt;

        void Awake()
        {
            IFileSystem fileSystem;

            _rt = ScriptEngine.CreateRuntime();
            _rt.AddSearchPath("node_modules");

            if (fileLoader == FileLoader.Resources)
            {
                fileSystem = new ResourcesFileSystem();
                _rt.AddSearchPath("dist");
            }
            else if (fileLoader == FileLoader.HMR)
            {
                Debug.LogWarningFormat("功能未完成");
                fileSystem = new HttpFileSystem(baseUrl);
            }
            else
            {
                fileSystem = new DefaultFileSystem();
                _rt.AddSearchPath("Assets/Examples/Scripts/out");
                // _rt.AddSearchPath("Assets/Examples/Scripts/dist");
            }

            _rt.withStacktrace = stacktrace;
            if (sourceMap)
            {
                _rt.EnableSourceMap();
            }
            _rt.Initialize(fileSystem, this);
        }

        void Update()
        {
            _rt.Update(Time.deltaTime);
        }

        void OnDestroy()
        {
            _rt.Destroy();
        }

        public void OnBind(ScriptRuntime runtime, TypeRegister register)
        {
            QuickJS.Extra.WebSocket.Bind(register);
            QuickJS.Extra.XMLHttpRequest.Bind(register);
            QuickJS.Extra.DOMCompatibleLayer.Bind(register);
            QuickJS.Extra.NodeCompatibleLayer.Bind(register);
        }

        public void OnComplete(ScriptRuntime runtime)
        {
            _rt.EvalMain("main.js");
        }
    }
}