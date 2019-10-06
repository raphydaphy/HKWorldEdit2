using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Hash128 = AssetsTools.NET.Hash128;

namespace Assets.Bundler
{
    public class ReferenceCrawler
    {
        //we use two files to isolate textures, audioclips, shaders, etc. because unity will corrupt them
        const string SCENE_LEVEL_NAME = "editorscene"; //give a name to the memory bundle which has no file name
        const string ASSET_LEVEL_NAME = "editorasset";
        public Dictionary<AssetID, AssetID> references; //game space to scene space
        public List<AssetsReplacer> sceneReplacers;
        public List<AssetsReplacer> assetReplacers;
        public List<AssetsReplacer> sceneMonoReplacers;
        private AssetsManager am;
        private int sceneId;
        private int assetId;
        public ReferenceCrawler(AssetsManager am)
        {
            this.am = am;
            sceneId = 1;
            assetId = 1;
            references = new Dictionary<AssetID, AssetID>();
            sceneReplacers = new List<AssetsReplacer>();
            assetReplacers = new List<AssetsReplacer>();
            sceneMonoReplacers = new List<AssetsReplacer>();
        }
        public void FindReferences(AssetsFileInstance inst, AssetFileInfoEx inf)
        {
            AssetTypeValueField baseField = am.GetATI(inst.file, inf).GetBaseField();
            FindReferencesRecurse(inst, baseField);
        }
        public void ReplaceReferences(AssetsFileInstance inst, AssetFileInfoEx inf, long pathId)
        {
            AssetTypeValueField baseField = am.GetATI(inst.file, inf).GetBaseField();
            ReplaceReferencesRecurse(inst, baseField, inf);
            FixAsset(inst, baseField, inf);
            byte[] baseFieldData;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                baseField.Write(w);
                baseFieldData = ms.ToArray();
            }
            AssetsReplacer replacer = new AssetsReplacerFromMemory(0, (ulong)pathId, (int)inf.curFileType, 0xFFFF, baseFieldData);
            if (IsAsset(inf))
                assetReplacers.Add(replacer);
            else if (inf.curFileType == 0x72)
                sceneMonoReplacers.Add(replacer);
            else
                sceneReplacers.Add(replacer);
        }
        public void AddReplacer(byte[] data, int type, ushort monoType, bool isAsset)
        {
            int id = isAsset ? assetId : sceneId;
            AssetsReplacer replacer = new AssetsReplacerFromMemory(0, (ulong)id, type, monoType, data);
            if (IsAsset(type))
                assetReplacers.Add(replacer);
            else if (type == 0x72)
                sceneMonoReplacers.Add(replacer);
            else
                sceneReplacers.Add(replacer);
            if (isAsset) assetId++; else sceneId++;
        }
        public void AddReference(AssetID aid, bool isAsset)
        {
            string name = isAsset ? ASSET_LEVEL_NAME : SCENE_LEVEL_NAME;
            int id = isAsset ? assetId : sceneId;
            AssetID newAid = new AssetID(name, id);
            references.Add(aid, newAid);
            if (isAsset) assetId++; else sceneId++;
        }
        private void FindReferencesRecurse(AssetsFileInstance inst, AssetTypeValueField field)
        {
            foreach (AssetTypeValueField child in field.pChildren)
            {
                //not a value (ie not an int)
                if (!child.templateField.hasValue)
                {
                    //not null
                    if (child == null)
                        return;
                    //not array of values either
                    if (child.templateField.isArray && child.templateField.children[1].valueType != EnumValueTypes.ValueType_None)
                        break;
                    string typeName = child.templateField.type;
                    //is a pptr
                    if (typeName.StartsWith("PPtr<") && typeName.EndsWith(">") && child.childrenCount == 2)
                    {
                        int fileId = child.Get("m_FileID").GetValue().AsInt();
                        long pathId = child.Get("m_PathID").GetValue().AsInt64();

                        //not a null pptr
                        if (pathId == 0)
                            continue;

                        AssetID aid = AssetID.FromPPtr(inst, fileId, pathId);

                        AssetsManager.AssetExternal ext = am.GetExtAsset(inst, fileId, pathId);

                        //not already visited and not a gameobject or monobehaviour
                        if (references.ContainsKey(aid) || ext.info.curFileType == 0x01)// || ext.info.curFileType == 0x72)
                            continue;

                        AddReference(aid, IsAsset(ext.info));

                        //recurse through dependencies
                        FindReferencesRecurse(ext.file, ext.instance.GetBaseField());
                    }
                    FindReferencesRecurse(inst, child);
                }
            }
        }
        private void ReplaceReferencesRecurse(AssetsFileInstance inst, AssetTypeValueField field, AssetFileInfoEx inf)
        {
            foreach (AssetTypeValueField child in field.pChildren)
            {
                //not a value (ie int)
                if (!child.templateField.hasValue)
                {
                    //not null
                    if (child == null)
                        return;
                    //not array of values either
                    if (child.templateField.isArray && child.templateField.children[1].valueType != EnumValueTypes.ValueType_None)
                        break;
                    string typeName = child.templateField.type;
                    //is a pptr
                    if (typeName.StartsWith("PPtr<") && typeName.EndsWith(">") && child.childrenCount == 2)
                    {
                        if (typeName == "PPtr<MonoScript>")
                            continue;

                        int fileId = child.Get("m_FileID").GetValue().AsInt();
                        long pathId = child.Get("m_PathID").GetValue().AsInt64();

                        //not a null pptr
                        if (pathId == 0)
                            continue;

                        AssetID aid = AssetID.FromPPtr(inst, fileId, pathId);
                        //not already visited
                        if (references.ContainsKey(aid))
                        {
                            AssetID id = references[aid];
                            //normally, I would've just checked if the path names
                            //matched up, but this is faster than looking up names
                            //I check type of this asset and compare with the name
                            //of the assetid to see if it should be itself or if
                            //it should be the dependency file
                            bool isSelfAsset = IsAsset(inf);
                            bool isDepAsset = id.fileName == ASSET_LEVEL_NAME;
                            int newFileId = isDepAsset ^ isSelfAsset ? 1 : 0;

                            child.Get("m_FileID").GetValue().Set(newFileId);
                            child.Get("m_PathID").GetValue().Set(id.pathId);
                        }
                        else
                        {
                            child.Get("m_FileID").GetValue().Set(0);
                            child.Get("m_PathID").GetValue().Set(0);
                        }
                    }
                    ReplaceReferencesRecurse(inst, child, inf);
                }
            }
        }

        private void FixAsset(AssetsFileInstance inst, AssetTypeValueField field, AssetFileInfoEx inf) {
            var type = field.GetFieldType();
            if (type.Equals("MonoBehaviour")) {
                try {
                    var m_Script = field.Get("m_Script"); // m_Script PPtr<MonoScript>
                    var m_GameObject = field.Get("m_GameObject"); // m_GameObject: PPtr<GameObject>

                    if (m_Script == null || m_GameObject == null) {
                        Debug.LogWarning("Null m_Script or m_GameObject under MonoBehaviour!");
                        return;
                    }

                    var gameObjectFileID = m_GameObject.Get("m_FileID").GetValue();
                    var gameObjectPathID = m_GameObject.Get("m_PathID").GetValue();

                    //Debug.Log("GameObject File ID: " + gameObjectFileID.AsInt());
                    //Debug.Log("GameObject Path ID: " + gameObjectPathID.AsInt64());

                    var scriptAssetInstance = am.GetExtAsset(inst, m_Script).instance;
                    var gameObjectAssetInstance = am.GetExtAsset(inst, m_GameObject).instance;

                    if (scriptAssetInstance == null || gameObjectAssetInstance == null) {
                        Debug.LogWarning("Null MonoScript or GameObject asset instance!");
                        return;
                    }

                    var gameObjectBaseField = gameObjectAssetInstance.GetBaseField();
                    var scriptBaseField = scriptAssetInstance.GetBaseField();

                    var className = scriptBaseField.Get("m_ClassName").GetValue();
                    var assemblyName = scriptBaseField.Get("m_AssemblyName").GetValue();

                    if (assemblyName.AsString().Equals("Assembly-CSharp.dll")) {
                        assemblyName.Set("HKCode.dll");
                    }

                    var gameObjectName = gameObjectBaseField.Get("m_Name").GetValue();
                    if (gameObjectName == null) return;

                    Debug.Log("GameObject " + gameObjectName.AsString() + " has " + ConjugateMonoScript(assemblyName, className));
                }
                catch (NullReferenceException e) {
                    Debug.LogError(e.StackTrace);
                }
            }
            if (inf.curFileType == 0x01) //fix gameobject
            {
                AssetTypeValueField ComponentArray = field.Get("m_Component").Get("Array");

                //remove all null pointers
                List<AssetTypeValueField> newFields = ComponentArray.pChildren.Where(f =>
                    f.pChildren[0].pChildren[1].GetValue().AsInt64() != 0
                ).ToList();

                //add editdiffer monobehaviour
                newFields.Add(CreatePPtrField(0, sceneId)); //this will be pathId that the below will go into
                AssetID aid = AssetID.FromPPtr(inst, 0, (long)inf.index);
                AddReplacer(CreateEditDifferMonoBehaviour(references[aid].pathId, inst, ComponentArray, aid), 0x72, 0x0000, false);

                uint newSize = (uint)newFields.Count;
                ComponentArray.SetChildrenList(newFields.ToArray(), newSize);
                ComponentArray.GetValue().Set(new AssetTypeArray() { size = newSize });
            }
            else if (inf.curFileType == 0x1c) //fix texture2d
            {
                AssetTypeValueField path = field.Get("m_StreamData").Get("path");
                string pathString = path.GetValue().AsString();
                string directory = Path.GetDirectoryName(inst.path);
                string fixedPath = Path.Combine(directory, pathString);
                path.GetValue().Set(fixedPath);
            }
            else if (inf.curFileType == 0x53) //fix audioclip
            {
                AssetTypeValueField path = field.Get("m_Resource").Get("m_Source");
                string pathString = path.GetValue().AsString();
                string directory = Path.GetDirectoryName(inst.path);
                string fixedPath = Path.Combine(directory, pathString);
                path.GetValue().Set(fixedPath);
            }
            else if (inf.curFileType == 0x72)
            {
                AssetTypeValueField m_Script = field.Get("m_Script");
                //convert scene pptrs to game ones
                AssetTypeValueField scriptBaseField = am.GetExtAsset(inst, m_Script).instance.GetBaseField();
                foreach (AssetTypeValueField v in scriptBaseField.pChildren) {
                    var fieldName = v.templateField.name;
                    if (fieldName != "m_ClassName" &&
                        fieldName != "m_Namespace" && 
                        fieldName != "m_AssemblyName" && 
                        fieldName != "m_Name" && 
                        fieldName != "m_ExecutionOrder" && 
                        fieldName != "m_IsEditorScript" &&
                        fieldName != "m_PropertiesHash") {
                        UnityEngine.Debug.Log("(MonoScript Child) Type: " + v.templateField.type + " Name: " + fieldName);
                    }
                }
                var className = scriptBaseField.Get("m_ClassName").GetValue();
                var assemblyName = scriptBaseField.Get("m_AssemblyName").GetValue();
                var propHash = scriptBaseField.Get("m_PropertiesHash");
                if (assemblyName.AsString().Equals("Assembly-CSharp.dll")) {
                    assemblyName.Set("HKCode.dll");
                }
                //UnityEngine.Debug.Log(ConjugateMonoScript(assemblyName, className));
            }
        }

        private string ConjugateMonoScript(AssetTypeValue assemblyName, AssetTypeValue className) {
            return "MonoScript: " + assemblyName.AsString() + " / " + className.AsString();
        }

        private byte[] CreateEditDifferMonoBehaviour(long goPid, AssetsFileInstance inst, AssetTypeValueField componentArray, AssetID origGoPptr)
        {
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                w.Write(0);
                w.Write(goPid);
                w.Write(1);
                w.Write(2);
                w.Write((long)11500000);
                w.WriteCountStringInt32("");
                w.Align();

                w.Write(0);
                w.Write(origGoPptr.pathId);
                w.Write(origGoPptr.pathId);
                w.Write(0);

                List<AssetID> filteredComponents = new List<AssetID>();
                for (uint i = 0; i < componentArray.GetValue().AsArray().size; i++)
                {
                    AssetTypeValueField component = componentArray[i].Get("component");
                    int m_FileID = component.Get("m_FileID").GetValue().AsInt();
                    long m_PathID = component.Get("m_PathID").GetValue().AsInt64();
                    //convert scene pptrs to game ones
                    AssetID id = new AssetID(SCENE_LEVEL_NAME, m_PathID);
                    AssetID oldId = references.FirstOrDefault(r => r.Value.Equals(id)).Key;
                    if (m_PathID != 0 && m_FileID == 0) //not removed (monobehaviour) and in the current file
                        filteredComponents.Add(oldId);
                }
                w.Write(filteredComponents.Count);
                foreach (AssetID id in filteredComponents)
                {
                    w.WriteCountStringInt32(id.fileName);
                    w.Align();
                    w.Write(id.pathId);
                }

                w.Write(0);
                data = ms.ToArray();
            }
            return data;
        }

        //time to update the api I guess
        private AssetTypeValueField CreatePPtrField(int fileId, long pathId)
        {
            AssetTypeTemplateField pptrTemp = new AssetTypeTemplateField();
            pptrTemp.FromClassDatabase(am.classFile, AssetHelper.FindAssetClassByID(am.classFile, 0x01), 5); //[5] PPtr<Component> component
            AssetTypeValueField fileVal = new AssetTypeValueField()
            {
                templateField = pptrTemp.children[0],
                childrenCount = 0,
                pChildren = null,
                value = new AssetTypeValue(EnumValueTypes.ValueType_Int32, fileId)
            };
            AssetTypeValueField pathVal = new AssetTypeValueField()
            {
                templateField = pptrTemp.children[1],
                childrenCount = 0,
                pChildren = null,
                value = new AssetTypeValue(EnumValueTypes.ValueType_Int64, pathId)
            };
            AssetTypeValueField pptrVal = new AssetTypeValueField()
            {
                templateField = pptrTemp,
                childrenCount = 2,
                pChildren = new AssetTypeValueField[] { fileVal, pathVal },
                value = new AssetTypeValue(EnumValueTypes.ValueType_None, null)
            };
            return pptrVal;
        }

        private bool IsAsset(AssetFileInfoEx inf)
        {
            return inf.curFileType == 0x1c || inf.curFileType == 0x30 || inf.curFileType == 0x53;
        }
        private bool IsAsset(int id)
        {
            return id == 0x1c || id == 0x30 || id == 0x53;
        }
    }
}
