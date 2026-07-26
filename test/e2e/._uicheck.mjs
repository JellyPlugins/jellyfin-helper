import { chromium } from '@playwright/test';
import { readFileSync } from 'node:fs';
const auth = JSON.parse(readFileSync('./setup/auth.json','utf8'));
const base = auth.baseUrl.replace(/\/$/,'');
const b = await chromium.launch();
const page = await b.newPage();
const info = await (await page.request.get(`${base}/System/Info/Public`)).json();
await page.goto(`${base}/web/index.html`);
await page.evaluate(({base,token,userId,serverId})=>{
  localStorage.setItem('jellyfin_credentials', JSON.stringify({Servers:[{manualAddress:base,Id:serverId,AccessToken:token,UserId:userId,DateLastAccessed:Date.now(),LastConnectionMode:1}]}));
  localStorage.setItem('enableAutoLogin','true');
},{base,token:auth.token,userId:auth.userId,serverId:info.Id});
await page.goto(`${base}/web/index.html#!/configurationpage?name=${encodeURIComponent('Jellyfin Helper')}`);
try {
  await page.locator('.tab-bar').waitFor({state:'visible',timeout:30000});
  console.log('RESULT: tab-bar VISIBLE — UI auth fix works');
} catch {
  const signIn = await page.locator('text=Please sign in').count().catch(()=>0);
  console.log('RESULT: tab-bar NOT found. sign-in present:', signIn>0);
}
await b.close();
