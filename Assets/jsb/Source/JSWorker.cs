using System;
using System.IO;
using System.Text;
using System.Net;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuickJS
{
    using QuickJS;
    using QuickJS.IO;
    using QuickJS.Utils;
    using QuickJS.Native;
    using QuickJS.Binding;

    public class JSWorker : Values, IScriptFinalize
    {
        private JSValue _self; // 在 main thread 中的 worker 自身

        private Thread _thread;
        private ScriptRuntime _parentRuntime;
        private ScriptRuntime _runtime;
        private Queue<IO.ByteBuffer> _inbox = new Queue<ByteBuffer>();

        private JSWorker()
        {
        }

        private void Release()
        {
            lock (_inbox)
            {
                while (_inbox.Count != 0)
                {
                    var buf = _inbox.Dequeue();
                    if (buf == null)
                    {
                        break;
                    }
                    buf.Release();
                }
            }
        }

        // 在主线程回调
        public void OnJSFinalize()
        {
            Release();
        }

        // 在主线程回调
        private void OnParentDestroy(ScriptRuntime parent)
        {
            if (!_self.IsUndefined())
            {
                _parentRuntime.FreeValue(_self);
                _self = JSApi.JS_UNDEFINED;
            }
            _runtime.Shutdown();
        }

        private void OnWorkerAfterDestroy(int id)
        {
            Release();
        }

        private void Start(JSContext ctx, JSValue value, string scriptPath)
        {
            var parent = ScriptEngine.GetRuntime(ctx);
            var runtime = parent.CreateWorker();

            if (runtime == null)
            {
                throw new NullReferenceException();
            }

            _self = JSApi.JS_DupValue(ctx, value);
            _parentRuntime = parent;
            _parentRuntime.OnDestroy += OnParentDestroy;

            _runtime = runtime;
            _runtime.OnAfterDestroy += OnWorkerAfterDestroy;
            RegisterGlobalObjects();
            _runtime.EvalMain(scriptPath);

            _thread = new Thread(new ThreadStart(Run));
            _thread.Priority = ThreadPriority.Lowest;
            _thread.IsBackground = true;
            _thread.Start();
        }

        private void RegisterGlobalObjects()
        {
            var context = _runtime.GetMainContext();
            var db = context.GetTypeDB();
            var globalObject = context.GetGlobalObject();
            {
                var propName = context.GetAtom("postMessage");
                var postMessage = db.NewDynamicMethod(propName, _js_self_postMessage);
                JSApi.JS_DefinePropertyValue(context, globalObject, propName, postMessage, JSPropFlags.DEFAULT);
            }
            {
                var propName = context.GetAtom("onmessage");
                JSApi.JS_DefinePropertyValue(context, globalObject, propName, JSApi.JS_NULL, JSPropFlags.JS_PROP_C_W_E);
            }
            JSApi.JS_FreeValue(context, globalObject);
        }

        private void Run()
        {
            var tick = Environment.TickCount;
            var list = new List<IO.ByteBuffer>();
            var context = _runtime.GetMainContext();
            var globalObject = context.GetGlobalObject();
            var onmessage = JSApi.JS_GetPropertyStr(context, globalObject, "onmessage");
            JSContext ctx = context;
            var logger = _runtime.GetLogger();

            while (_runtime.isRunning)
            {
                lock (_inbox)
                {
                    list.AddRange(_inbox);
                    _inbox.Clear();
                }

                if (list.Count == 0)
                {
                    Thread.Yield();
                }
                else
                {
                    for (int i = 0, count = list.Count; i < count; i++)
                    {
                        var byteBuffer = list[i];

                        unsafe
                        {
                            JSValue data;
                            fixed (byte* buf = byteBuffer.data)
                            {
                                data = JSApi.JS_ReadObject(ctx, buf, byteBuffer.readableBytes, 0);
                            }

                            if (data.IsException())
                            {
                                var exceptionString = ctx.GetExceptionString();
                                if (logger != null)
                                {
                                    logger.Write(LogLevel.Error, exceptionString);
                                }
                            }
                            else
                            {
                                var argv = stackalloc JSValue[1] { data };
                                var rval = JSApi.JS_Call(ctx, onmessage, globalObject, 1, argv);
                                JSApi.JS_FreeValue(ctx, rval);
                            }
                        }
                        byteBuffer.Release();
                    }
                    list.Clear();
                }

                var now = Environment.TickCount;
                var dt = now - tick;
                tick = now;

                _runtime.Update(dt);
            }

            JSApi.JS_FreeValue(ctx, onmessage);
            JSApi.JS_FreeValue(ctx, globalObject);
            _runtime.Destroy();
        }

        // 在主线程回调
        private static unsafe void _PostMessage(ScriptRuntime runtime, JSAction action)
        {
            var worker = action.worker;

            try
            {
                if (worker._runtime.isRunning && worker._parentRuntime.isRunning)
                {
                    var context = runtime.GetMainContext();
                    var ctx = (JSContext)context;
                    var onmessage = JSApi.JS_GetProperty(ctx, worker._self, context.GetAtom("onmessage"));
                    if (onmessage.IsException())
                    {
                        var exceptionString = ctx.GetExceptionString();
                        var logger = runtime.GetLogger();
                        if (logger != null)
                        {
                            logger.Write(LogLevel.Error, exceptionString);
                        }
                    }
                    else
                    {
                        if (JSApi.JS_IsFunction(ctx, onmessage) == 1)
                        {
                            // read object => jsvalue
                            JSValue data;
                            fixed (byte* buf = action.buffer.data)
                            {
                                data = JSApi.JS_ReadObject(ctx, buf, action.buffer.readableBytes, 0);
                            }

                            if (data.IsException())
                            {
                                var exceptionString = ctx.GetExceptionString();
                                var logger = runtime.GetLogger();
                                if (logger != null)
                                {
                                    logger.Write(LogLevel.Error, exceptionString);
                                }
                            }
                            else
                            {
                                var argv = stackalloc JSValue[1] { data };
                                var rval = JSApi.JS_Call(ctx, onmessage, worker._self, 1, argv);
                                JSApi.JS_FreeValue(ctx, rval);
                            }
                        }
                        else
                        {
                            // not function
                        }
                        JSApi.JS_FreeValue(ctx, onmessage);
                    }
                }
            }
            finally
            {
                action.buffer.Release();
            }
        }

        private JSValue _js_self_postMessage(JSContext ctx, JSValue this_obj, int argc, JSValue[] argv)
        {
            try
            {
                // ctx is woker runtime
                if (!_runtime.isRunning)
                {
                    return JSApi.JS_ThrowInternalError(ctx, "worker is not running");
                }

                if (argc < 1)
                {
                    return JSApi.JS_ThrowInternalError(ctx, "invalid parameter");
                }

                size_t psize;
                var dataStore = JSApi.JS_WriteObject(ctx, out psize, argv[0], 0);
                if (dataStore == IntPtr.Zero)
                {
                    return JSApi.JS_ThrowInternalError(ctx, "fail to write object");
                }

                var buffer = ScriptEngine.AllocSharedByteBuffer(psize);
                buffer.WriteBytes(dataStore, psize);
                JSApi.js_free(ctx, dataStore);

                this._parentRuntime.EnqueueAction(new JSAction()
                {
                    worker = this,
                    buffer = buffer,
                    callback = _PostMessage,
                });
                return JSApi.JS_UNDEFINED;
            }
            catch (Exception exception)
            {
                return JSApi.ThrowException(ctx, exception);
            }
        }

        private static JSValue _js_worker_ctor(JSContext ctx, JSValue new_target, int argc, JSValue[] argv, int magic)
        {
            if (argc < 1 || !argv[0].IsString())
            {
                return JSApi.JS_ThrowInternalError(ctx, "invalid parameter");
            }

            var scriptPath = JSApi.GetString(ctx, argv[0]);
            var worker = new JSWorker();
            var val = NewBridgeClassObject(ctx, new_target, worker, magic);
            try
            {
                if (val.IsObject())
                {
                    worker.Start(ctx, val, scriptPath);
                }
            }
            catch (Exception e)
            {
                JSApi.JS_FreeValue(ctx, val);
                return JSApi.ThrowException(ctx, e);
            }
            return val;
        }

        // main thread post message to worker
        private static JSValue _js_worker_postMessage(JSContext ctx, JSValue this_obj, int argc, JSValue[] argv)
        {
            try
            {
                JSWorker self;
                if (!js_get_classvalue(ctx, this_obj, out self))
                {
                    throw new ThisBoundException();
                }

                if (argc < 1)
                {
                    return JSApi.JS_ThrowInternalError(ctx, "invalid parameter");
                }

                if (!self._runtime.isRunning)
                {
                    return JSApi.JS_ThrowInternalError(ctx, "worker is not running");
                }

                size_t psize;
                var dataStore = JSApi.JS_WriteObject(ctx, out psize, argv[0], 0);
                if (dataStore == IntPtr.Zero)
                {
                    return JSApi.JS_ThrowInternalError(ctx, "fail to write object");
                }

                var buffer = ScriptEngine.AllocSharedByteBuffer(psize);
                buffer.WriteBytes(dataStore, psize);
                JSApi.js_free(ctx, dataStore);

                lock (self._inbox)
                {
                    if (self._runtime.isRunning)
                    {
                        self._inbox.Enqueue(buffer);
                    }
                    else
                    {
                        buffer.Release();
                    }
                }

                return JSApi.JS_UNDEFINED;
            }
            catch (Exception exception)
            {
                return JSApi.ThrowException(ctx, exception);
            }
        }

        private static JSValue _js_worker_terminate(JSContext ctx, JSValue this_obj, int argc, JSValue[] argv)
        {
            try
            {
                JSWorker self;
                if (!js_get_classvalue(ctx, this_obj, out self))
                {
                    throw new ThisBoundException();
                }

                self._runtime.Shutdown();
                return JSApi.JS_UNDEFINED;
            }
            catch (Exception exception)
            {
                return JSApi.ThrowException(ctx, exception);
            }
        }

        public static void Bind(TypeRegister register)
        {
            var ns = register.CreateNamespace();
            var cls = ns.CreateClass("Worker", typeof(JSWorker), _js_worker_ctor);
            cls.AddMethod(false, "postMessage", _js_worker_postMessage, 1);
            cls.AddMethod(false, "terminate", _js_worker_terminate);
            cls.Close();
            ns.Close();
        }
    }
}
