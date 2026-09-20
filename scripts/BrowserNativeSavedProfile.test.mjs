import assert from 'node:assert/strict';
import test from 'node:test';
import { savedProfileFailure } from './Test-BrowserNativeSavedProfile.mjs';

test('saved-profile diagnostics retain only assertion kind and fixture coordinates',()=>{
  const error=new assert.AssertionError({actual:'private-pairing',expected:'private-config',message:'private-message'});
  error.stack='AssertionError: private-message\n at savedProfileAcceptance (file:///private/root/Test-BrowserNativeSavedProfile.mjs:51:12)';
  assert.deepEqual(savedProfileFailure(error),{kind:'assertion_failed',line:51,column:12});
});
test('execution diagnostics never retain messages, child output or foreign stack paths',()=>{
  const error=Object.assign(new Error('private-message'),{stdout:'private-output',stderr:'private-error',code:'private-code'});
  error.stack='Error: private-message\n at file:///private/other.mjs:9:2';
  assert.deepEqual(savedProfileFailure(error),{kind:'execution_failed'});
});
test('numeric coordinates support Windows stack paths without publishing paths',()=>{
  const error=new Error('private-message');
  error.stack='Error: private-message\n at savedProfileAcceptance (D:\\private\\Test-BrowserNativeSavedProfile.mjs:173:5)';
  assert.deepEqual(savedProfileFailure(error),{kind:'execution_failed',line:173,column:5});
});
test('non-Error throws cannot add diagnostic fields',()=>{
  assert.deepEqual(savedProfileFailure({message:'private-message',stack:'private-stack',actual:'private-output'}),{kind:'execution_failed'});
  assert.deepEqual(savedProfileFailure(null),{kind:'execution_failed'});
});
