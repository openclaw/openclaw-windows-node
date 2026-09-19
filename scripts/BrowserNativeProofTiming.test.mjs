import test from 'node:test';
import assert from 'node:assert/strict';
import { recordCompletion } from './BrowserNativeProofTiming.mjs';
for (const retry of [false,true]) test('retirement-before-frame remains detectable, retry='+retry,async()=>{
  let clock=0;
  if(retry)assert.equal((await recordCompletion(Promise.resolve({ok:false,code:'busy'}),()=>++clock)).value.code,'busy');
  const retirement=Promise.withResolvers();
  const recorded=recordCompletion(retirement.promise,()=>++clock);
  retirement.resolve({ok:true});await Promise.resolve();
  const firstFrameAt=++clock;const completed=await recorded;
  assert.throws(()=>assert.ok(firstFrameAt<=completed.completedAt));
});
test('frame-before-retirement is accepted',async()=>{
  let clock=0;const firstFrameAt=++clock;
  const completed=await recordCompletion(Promise.resolve({ok:true}),()=>++clock);
  assert.ok(firstFrameAt<=completed.completedAt);
});
