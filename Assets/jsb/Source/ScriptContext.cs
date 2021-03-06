﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using QuickJS.Binding;
using QuickJS.Native;
using QuickJS.Utils;
using Object = UnityEngine.Object;

namespace QuickJS
{
    public partial class ScriptContext
    {
        public event Action<ScriptContext> OnDestroy;
        public event Action<int> OnAfterDestroy;

        private ScriptRuntime _runtime;
        private int _contextId;
        private JSContext _ctx;
        private AtomCache _atoms;
        private JSValue _moduleCache; // commonjs module cache
        private JSValue _require; // require function object 
        private CoroutineManager _coroutines;
        private bool _isValid;
        private Regex _stRegex;

        private JSValue _globalObject;
        private JSValue _operatorCreate;
        private JSValue _numberConstructor;
        private JSValue _stringConstructor;

        // id = context slot index + 1
        public int id { get { return _contextId; } }

        public ScriptContext(ScriptRuntime runtime, int contextId)
        {
            _isValid = true;
            _runtime = runtime;
            _contextId = contextId;
            _ctx = JSApi.JS_NewContext(_runtime);
            JSApi.JS_SetContextOpaque(_ctx, (IntPtr)_contextId);
            JSApi.JS_AddIntrinsicOperators(_ctx);
            _atoms = new AtomCache(_ctx);
            _moduleCache = JSApi.JS_NewObject(_ctx);

            _globalObject = JSApi.JS_GetGlobalObject(_ctx);
            _numberConstructor = JSApi.JS_GetProperty(_ctx, _globalObject, JSApi.JS_ATOM_Number);
            _stringConstructor = JSApi.JS_GetProperty(_ctx, _globalObject, JSApi.JS_ATOM_String);
            _operatorCreate = JSApi.JS_UNDEFINED;

            var operators = JSApi.JS_GetProperty(_ctx, _globalObject, JSApi.JS_ATOM_Operators);
            if (!operators.IsNullish())
            {
                if (operators.IsException())
                {
                    _ctx.print_exception();
                }
                else
                {
                    var create = JSApi.JS_GetProperty(_ctx, operators, GetAtom("create"));
                    JSApi.JS_FreeValue(_ctx, operators);
                    if (create.IsException())
                    {
                        _ctx.print_exception();
                    }
                    else
                    {
                        if (JSApi.JS_IsFunction(_ctx, create) == 1)
                        {
                            _operatorCreate = create;
                        }
                        else
                        {
                            JSApi.JS_FreeValue(_ctx, create);
                        }
                    }
                }
            }
        }

        public bool IsValid()
        {
            return _isValid;
        }

        public CoroutineManager GetCoroutineManager()
        {
            if (_isValid)
            {
                if (_coroutines == null)
                {
                    var go = _runtime.GetContainer();
                    if (go != null)
                    {
                        _coroutines = go.AddComponent<CoroutineManager>();
                    }
                }
            }
            return _coroutines;
        }

        public JSValue Yield(object awaitObject)
        {
            GetCoroutineManager();
            if (_coroutines != null)
            {
                return _coroutines.Yield(this, awaitObject);
            }

            return JSApi.JS_ThrowInternalError(_ctx, "no async manager");
        }

        public TimerManager GetTimerManager()
        {
            return _runtime.GetTimerManager();
        }

        public IScriptLogger GetLogger()
        {
            return _runtime.GetLogger();
        }

        public TypeDB GetTypeDB()
        {
            return _runtime.GetTypeDB();
        }

        public ObjectCache GetObjectCache()
        {
            return _runtime.GetObjectCache();
        }

        public ScriptRuntime GetRuntime()
        {
            return _runtime;
        }

        public bool IsContext(JSContext ctx)
        {
            return ctx == _ctx;
        }

        //NOTE: 返回值不需要释放, context 销毁时会自动释放所管理的 Atom
        public JSAtom GetAtom(string name)
        {
            return _atoms.GetAtom(name);
        }

        public void Destroy()
        {
            if (!_isValid)
            {
                return;
            }
            _isValid = false;

            try
            {
                OnDestroy?.Invoke(this);
            }
            catch (Exception e)
            {
                _runtime.GetLogger()?.WriteException(e);
            }
            _atoms.Clear();

            JSApi.JS_FreeValue(_ctx, _numberConstructor);
            JSApi.JS_FreeValue(_ctx, _stringConstructor);
            JSApi.JS_FreeValue(_ctx, _globalObject);
            JSApi.JS_FreeValue(_ctx, _operatorCreate);

            JSApi.JS_FreeValue(_ctx, _moduleCache);
            JSApi.JS_FreeValue(_ctx, _require);
            JSApi.JS_FreeContext(_ctx);
            var id = _contextId;
            _contextId = -1;

            if (_coroutines != null)
            {
                Object.DestroyImmediate(_coroutines);
                _coroutines = null;
            }

            _ctx = JSContext.Null;
            try
            {
                OnAfterDestroy?.Invoke(id);
            }
            catch (Exception e)
            {
                _runtime.GetLogger()?.WriteException(e);
            }
        }

        public void FreeValue(JSValue value)
        {
            _runtime.FreeValue(value);
        }

        public void FreeValues(JSValue[] values)
        {
            _runtime.FreeValues(values);
        }

        ///<summary>
        /// 获取全局对象 (增加引用计数)
        ///</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JSValue GetGlobalObject()
        {
            return JSApi.JS_DupValue(_ctx, _globalObject);
        }

        ///<summary>
        /// 获取 string.constructor (增加引用计数)
        ///</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JSValue GetStringConstructor()
        {
            return JSApi.JS_DupValue(_ctx, _stringConstructor);
        }

        ///<summary>
        /// 获取 number.constructor (增加引用计数)
        ///</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JSValue GetNumberConstructor()
        {
            return JSApi.JS_DupValue(_ctx, _numberConstructor);
        }

        ///<summary>
        /// 获取 operator.create (增加引用计数)
        ///</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JSValue GetOperatorCreate()
        {
            return JSApi.JS_DupValue(_ctx, _operatorCreate);
        }

        //NOTE: 返回值需要调用者 free 
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JSValue _get_commonjs_module(string module_id)
        {
            var prop = GetAtom(module_id);
            return JSApi.JS_GetProperty(_ctx, _moduleCache, prop);
        }

        //NOTE: 返回值需要调用者 free
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JSValue _new_commonjs_module(string module_id, JSValue exports_obj, bool loaded)
        {
            var module_obj = JSApi.JS_NewObject(_ctx);
            var prop = GetAtom(module_id);

            JSApi.JS_SetProperty(_ctx, _moduleCache, prop, JSApi.JS_DupValue(_ctx, module_obj));
            JSApi.JS_SetProperty(_ctx, module_obj, GetAtom("cache"), JSApi.JS_DupValue(_ctx, _moduleCache));
            JSApi.JS_SetProperty(_ctx, module_obj, GetAtom("loaded"), JSApi.JS_NewBool(_ctx, loaded));
            JSApi.JS_SetProperty(_ctx, module_obj, GetAtom("exports"), JSApi.JS_DupValue(_ctx, exports_obj));

            return module_obj;
        }

        public static void Bind(TypeRegister register)
        {
            var ns_jsb = register.CreateNamespace("jsb");

            ns_jsb.AddFunction("DoFile", _DoFile, 1);
            ns_jsb.AddFunction("AddSearchPath", _AddSearchPath, 1);
            ns_jsb.AddFunction("Yield", yield_func, 1);
            ns_jsb.AddFunction("ToArray", to_js_array, 1);
            ns_jsb.AddFunction("ToArrayBuffer", to_js_array_buffer, 1);
            ns_jsb.AddFunction("ToBytes", to_cs_bytes, 1);
            ns_jsb.AddFunction("Import", js_import_type, 2);

            {
                var ns_jsb_hotfix = ns_jsb.CreateNamespace("hotfix");
                ns_jsb_hotfix.AddFunction("replace_single", hotfix_replace_single, 2);
                ns_jsb_hotfix.AddFunction("before_single", hotfix_before_single, 2);
                // ns_jsb_hotfix.AddFunction("replace", hotfix_replace, 2);
                // ns_jsb_hotfix.AddFunction("before", hotfix_before);
                // ns_jsb_hotfix.AddFunction("after", hotfix_after);
                ns_jsb_hotfix.Close();
            }
            ns_jsb.Close();
        }

        public unsafe void EvalMain(byte[] source, string fileName)
        {
            var tagValue = ScriptRuntime.TryReadByteCodeTagValue(source);
            if (tagValue == ScriptRuntime.BYTECODE_ES6_MODULE_TAG)
            {
                throw new Exception("es6 module bytecode as main is unsupported");
            }

            var dirname = PathUtils.GetDirectoryName(fileName);
            var filename_bytes = TextUtils.GetNullTerminatedBytes(fileName);
            var filename_atom = GetAtom(fileName);
            var dirname_atom = GetAtom(dirname);

            var exports_obj = JSApi.JS_NewObject(_ctx);
            var require_obj = JSApi.JS_DupValue(_ctx, _require);
            var module_obj = _new_commonjs_module("", exports_obj, true);
            var filename_obj = JSApi.JS_AtomToString(_ctx, filename_atom);
            var dirname_obj = JSApi.JS_AtomToString(_ctx, dirname_atom);
            var require_argv = new JSValue[5] { exports_obj, require_obj, module_obj, filename_obj, dirname_obj };
            JSApi.JS_SetProperty(_ctx, require_obj, GetAtom("moduleId"), JSApi.JS_DupValue(_ctx, filename_obj));

            if (tagValue == ScriptRuntime.BYTECODE_COMMONJS_MODULE_TAG)
            {
                // bytecode
                fixed (byte* intput_ptr = source)
                {
                    var bytecodeFunc = JSApi.JS_ReadObject(_ctx, intput_ptr + sizeof(uint), source.Length - sizeof(uint), JSApi.JS_READ_OBJ_BYTECODE);

                    if (bytecodeFunc.tag == JSApi.JS_TAG_FUNCTION_BYTECODE)
                    {
                        var func_val = JSApi.JS_EvalFunction(_ctx, bytecodeFunc); // it's CallFree (bytecodeFunc)
                        if (JSApi.JS_IsFunction(_ctx, func_val) != 1)
                        {
                            JSApi.JS_FreeValue(_ctx, func_val);
                            FreeValues(require_argv);
                            throw new Exception("failed to eval bytecode module");
                        }

                        var rval = JSApi.JS_Call(_ctx, func_val, JSApi.JS_UNDEFINED);
                        JSApi.JS_FreeValue(_ctx, func_val);
                        if (rval.IsException())
                        {
                            _ctx.print_exception();
                        }
                        FreeValues(require_argv);
                        return;
                    }

                    JSApi.JS_FreeValue(_ctx, bytecodeFunc);
                    FreeValues(require_argv);
                    throw new Exception("failed to eval bytecode module");
                }
            }
            else
            {
                // source
                var input_bytes = TextUtils.GetShebangNullTerminatedCommonJSBytes(source);
                fixed (byte* input_ptr = input_bytes)
                fixed (byte* resolved_id_ptr = filename_bytes)
                {
                    var input_len = (size_t)(input_bytes.Length - 1);
                    var func_val = JSApi.JS_Eval(_ctx, input_ptr, input_len, resolved_id_ptr, JSEvalFlags.JS_EVAL_TYPE_GLOBAL | JSEvalFlags.JS_EVAL_FLAG_STRICT);
                    if (func_val.IsException())
                    {
                        FreeValues(require_argv);
                        _ctx.print_exception();
                        return;
                    }

                    if (JSApi.JS_IsFunction(_ctx, func_val) == 1)
                    {
                        var rval = JSApi.JS_Call(_ctx, func_val, JSApi.JS_UNDEFINED, require_argv.Length, require_argv);
                        if (rval.IsException())
                        {
                            JSApi.JS_FreeValue(_ctx, func_val);
                            FreeValues(require_argv);
                            _ctx.print_exception();
                            return;
                        }
                    }

                    JSApi.JS_FreeValue(_ctx, func_val);
                    FreeValues(require_argv);
                }
            }
        }

        public void EvalSourceFree(string source, string fileName)
        {
            EvalSourceFree(source, fileName, null);
        }

        public void EvalSourceFree(string source, string fileName, Action<JSContext, JSValue> onEvalReturn)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(source);
            EvalSourceFree(bytes, fileName, onEvalReturn);
        }

        public void EvalSourceFree(byte[] source, string fileName)
        {
            EvalSourceFree(source, fileName, null);
        }

        public void EvalSourceFree(byte[] source, string fileName, Action<JSContext, JSValue> onEvalReturn)
        {
            var jsValue = ScriptRuntime.EvalSource(_ctx, source, fileName, false);
            if (JSApi.JS_IsException(jsValue))
            {
                _ctx.print_exception();
            }
            else
            {
                onEvalReturn(_ctx, jsValue);
            }

            JSApi.JS_FreeValue(_ctx, jsValue);
        }

        public void RegisterBuiltins()
        {
            var ctx = (JSContext)this;
            var global_object = this.GetGlobalObject();
            {
                _require = JSApi.JSB_NewCFunction(ctx, ScriptRuntime.module_require, GetAtom("require"), 1, JSCFunctionEnum.JS_CFUNC_generic, 0);
                JSApi.JS_SetProperty(ctx, _require, GetAtom("moduleId"), JSApi.JS_NewString(ctx, ""));
                JSApi.JS_SetProperty(ctx, _require, GetAtom("cache"), JSApi.JS_DupValue(ctx, _moduleCache));
                JSApi.JS_SetProperty(ctx, global_object, GetAtom("require"), JSApi.JS_DupValue(ctx, _require));

                JSApi.JS_SetPropertyStr(ctx, global_object, "print", JSApi.JS_NewCFunctionMagic(ctx, _print, "print", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 0));
                var console = JSApi.JS_NewObject(ctx);
                {
                    JSApi.JS_SetPropertyStr(ctx, console, "log", JSApi.JS_NewCFunctionMagic(ctx, _print, "log", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 0));
                    JSApi.JS_SetPropertyStr(ctx, console, "info", JSApi.JS_NewCFunctionMagic(ctx, _print, "info", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 0));
                    JSApi.JS_SetPropertyStr(ctx, console, "debug", JSApi.JS_NewCFunctionMagic(ctx, _print, "debug", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 0));
                    JSApi.JS_SetPropertyStr(ctx, console, "warn", JSApi.JS_NewCFunctionMagic(ctx, _print, "warn", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 1));
                    JSApi.JS_SetPropertyStr(ctx, console, "error", JSApi.JS_NewCFunctionMagic(ctx, _print, "error", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 2));
                    JSApi.JS_SetPropertyStr(ctx, console, "assert", JSApi.JS_NewCFunctionMagic(ctx, _print, "assert", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 3));
                }
                JSApi.JS_SetPropertyStr(ctx, global_object, "console", console);

                var threading = JSApi.JS_NewObject(ctx);
                {
                    JSApi.JS_SetPropertyStr(ctx, threading, "sleep", JSApi.JS_NewCFunctionMagic(ctx, _sleep, "sleep", 1, JSCFunctionEnum.JS_CFUNC_generic_magic, 0));
                }
                JSApi.JS_SetPropertyStr(ctx, global_object, "threading", threading);
            }
            JSApi.JS_FreeValue(ctx, global_object);
        }

        private string js_source_position(JSContext ctx, string funcName, string fileName, int lineNumber)
        {
            return $"{funcName} ({fileName}:{lineNumber})";
        }

        public void AppendStacktrace(StringBuilder sb)
        {
            var ctx = _ctx;
            var globalObject = JSApi.JS_GetGlobalObject(ctx);
            var errorConstructor = JSApi.JS_GetProperty(ctx, globalObject, JSApi.JS_ATOM_Error);
            var errorObject = JSApi.JS_CallConstructor(ctx, errorConstructor);
            var stackValue = JSApi.JS_GetProperty(ctx, errorObject, JSApi.JS_ATOM_stack);
            var stack = JSApi.GetString(ctx, stackValue);

            if (!string.IsNullOrEmpty(stack))
            {
                var errlines = stack.Split('\n');
                if (_stRegex == null)
                {
                    _stRegex = new Regex(@"^\s+at\s(.+)\s\((.+\.js):(\d+)\)(.*)$", RegexOptions.Compiled);
                }
                for (var i = 0; i < errlines.Length; i++)
                {
                    var line = errlines[i];
                    var matches = _stRegex.Matches(line);
                    if (matches.Count == 1)
                    {
                        var match = matches[0];
                        if (match.Groups.Count >= 4)
                        {
                            var funcName = match.Groups[1].Value;
                            var fileName = match.Groups[2].Value;
                            var lineNumber = 0;
                            int.TryParse(match.Groups[3].Value, out lineNumber);
                            var extra = match.Groups.Count >= 5 ? match.Groups[4].Value : "";
                            var sroucePosition = (_runtime.OnSourceMap ?? js_source_position)(ctx, funcName, fileName, lineNumber);
                            sb.AppendLine($"    at {sroucePosition}{extra}");
                            continue;
                        }
                    }
                    sb.AppendLine(line);
                }
            }

            JSApi.JS_FreeValue(ctx, stackValue);
            JSApi.JS_FreeValue(ctx, errorObject);
            JSApi.JS_FreeValue(ctx, errorConstructor);
            JSApi.JS_FreeValue(ctx, globalObject);
        }

        public static implicit operator JSContext(ScriptContext sc)
        {
            return sc._ctx;
        }
    }
}