using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
namespace OutfitToggleGenerator {
[InitializeOnLoad] internal static class AWSceneUploadSelfCheck {
 static AWSceneUploadSelfCheck() { if(File.Exists("/private/tmp/aw-scene-test-request")) EditorApplication.delayCall += Run; }
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static async void Run(){
  File.Delete("/private/tmp/aw-scene-test-request");
  var previous=SceneManager.GetActiveScene();var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
  try{
   var root=new GameObject("CommonOnlyTest");SceneManager.MoveGameObjectToScene(root,scene);
   var avatar=root.AddComponent<VRCAvatarDescriptor>();
   var pipe=root.AddComponent<VRC.Core.PipelineManager>();pipe.blueprintId="avtr-local-test";
   var outfit=new GameObject("Outfit");outfit.transform.SetParent(root.transform,false);
   var hair=new GameObject("Hair");hair.transform.SetParent(root.transform,false);hair.SetActive(false);
   var menu=new GameObject("ExistingControls");menu.transform.SetParent(root.transform,false);menu.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarMenuItem>();
   EditorSceneManager.SaveScene(scene,"Assets/AWSceneUploadTemporaryTest.unity");
   var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.transform.SetParent(root.transform,false);
   string thumbnail=null;
   try {
    Check(AvatarWardrobeUpload.ResolveSceneUploadThumbnail(root,"remote-test",true)==null,"Existing remote thumbnail not preserved");
    var key=Guid.NewGuid().ToString("N");
    thumbnail=AvatarWardrobeUpload.ResolveSceneUploadThumbnail(root,key,false);
    Check(File.Exists(thumbnail)&&new FileInfo(thumbnail).Length>8,"No generated thumbnail");
    var bytes=File.ReadAllBytes(thumbnail);
    Check(bytes[0]==137&&bytes[1]==80&&bytes[2]==78&&bytes[3]==71,"Generated file is not PNG");
    Check(AvatarWardrobeUpload.ResolveSceneUploadThumbnail(null,key,false)==thumbnail,"Cached thumbnail was not reused");
   } finally { if(thumbnail!=null&&File.Exists(thumbnail))File.Delete(thumbnail);UnityEngine.Object.DestroyImmediate(cube); }
   int count=SceneManager.sceneCount;
   string stagedPath=null;
   for(int mode=0;mode<3;mode++){
    int failure=mode; bool caught=false;
    try { await AvatarWardrobeUpload.RunOnAvatarCopy(avatar,async clone=>{
      stagedPath=clone.scene.path;
      Check(!string.IsNullOrEmpty(stagedPath),"SDK received an untitled scene");
      Check(File.Exists(stagedPath),"Temporary scene was not saved");
      Check(!clone.scene.isDirty,"Build starts with a dirty temporary scene");
      Check(clone!=root,"Source passed to build");Check(clone.scene!=scene,"Clone stayed in source scene");
      Check(clone.transform.Find("Outfits")==null,"Created Outfits folder");
      Check(clone.transform.Find("Outfit").gameObject.activeSelf,"Enabled state lost");
      Check(!clone.transform.Find("Hair").gameObject.activeSelf,"Disabled state lost");
      Check(clone.transform.Find("ExistingControls").GetComponent<nadena.dev.modular_avatar.core.ModularAvatarMenuItem>()!=null,"Controls lost");
      clone.transform.Find("Hair").gameObject.SetActive(true);clone.GetComponent<VRC.Core.PipelineManager>().blueprintId="avtr-clone-only";
      await Task.Yield();
      if(failure==1)throw new InvalidOperationException("Simulated SDK failure");
      if(failure==2)throw new OperationCanceledException();
    }); }catch(InvalidOperationException e){if(mode!=1 || e.Message!="Simulated SDK failure") throw; caught=true;}catch(OperationCanceledException){if(mode!=2)throw;caught=true;}
    Check(caught==(mode!=0),"Failure/cancellation did not propagate");
    Check(!File.Exists(stagedPath),"Temporary scene file leaked");
    Check(SceneManager.sceneCount==count,"Leaked staging scene");Check(SceneManager.GetActiveScene()==scene,"Active scene not restored");
    Check(root.transform.childCount==3&&!hair.activeSelf&&outfit.activeSelf,"Source hierarchy/state changed");
    Check(pipe.blueprintId=="avtr-local-test","Source blueprint changed");
   }
   File.WriteAllText("/private/tmp/aw-scene-test-result","PASS: automatic thumbnail generation, PNG encoding, cached reuse, remote preservation; Common-only/no-Outfits staging, controls, enabled states, source Blueprint ID preservation, cleanup and active-scene restoration on success, failure and cancellation.");
  }catch(Exception e){File.WriteAllText("/private/tmp/aw-scene-test-result","FAIL: "+e);}
  finally{if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);EditorSceneManager.CloseScene(scene,true);AssetDatabase.DeleteAsset("Assets/AWSceneUploadTemporaryTest.unity");}
 }
}}
