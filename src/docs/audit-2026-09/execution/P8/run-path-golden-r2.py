import pathlib,subprocess,hashlib,json
repo=pathlib.Path('/Users/wojo/Dev/swift-bindings')
root=pathlib.Path(__file__).resolve().parent/'path-golden-r2'
root.mkdir(exist_ok=False)
target=repo/'src/Swift.Bindings.Sdk/Sdk/Sdk.targets'
current=target.read_bytes()
old=subprocess.check_output(['git','show','HEAD:src/Swift.Bindings.Sdk/Sdk/Sdk.targets'],cwd=repo)
(root/'current.targets').write_bytes(current)
(root/'old.targets').write_bytes(old)
args=['dotnet','test',str(repo/'src/Swift.Bindings/tests/UnitTests/Swift.Bindings.Unit.Tests.csproj'),'--no-build','--no-restore','--filter','FullyQualifiedName~GetNativeManifest_SourceDroppedWithWrapperMetadataTrue_FlowsExactExistingPaths|FullyQualifiedName~GetSwiftFrameworkSearchPaths_WrapperPathIsExactAndExists|FullyQualifiedName~GetNativeManifest_ProjectReferenceConsumerReceivesExactExistingPaths']
results={}
try:
 target.write_bytes(old)
 run=subprocess.run(args,cwd=repo,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
 (root/'old.log').write_text(run.stdout);results['old_exit']=run.returncode
finally:
 target.write_bytes(current)
run=subprocess.run(args,cwd=repo,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
(root/'current.log').write_text(run.stdout);results['current_exit']=run.returncode
results['sha256']={k:hashlib.sha256(v).hexdigest() for k,v in [('current.targets',current),('old.targets',old)]}
results['restored_exactly']=target.read_bytes()==current
results['command']=args
(root/'receipt.json').write_text(json.dumps(results,indent=2)+'\n')
print(json.dumps(results),flush=True)
assert results['old_exit']!=0 and results['current_exit']==0 and results['restored_exactly']
