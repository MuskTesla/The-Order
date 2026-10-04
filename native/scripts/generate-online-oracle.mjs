import fs from 'node:fs/promises'
import { createHash } from 'node:crypto'
import { NextMoeClient } from '../../electron/nextmoe.ts'
const fixed = '2026-10-04T00:00:00.000Z', RealDate = Date
globalThis.Date = class extends RealDate { constructor(...args) { super(...(args.length ? args : [fixed])) } static now() { return new RealDate(fixed).valueOf() } }
const work = (id='86') => ({object:'work',id,display_name:'Synthetic Original',localized:{'zh-Hans':{value:'合成作品'}},latin:'Romanized',titles:[{title:'別名',latin:'Alias',title_kind:'official',is_machine:false,lang:'ja'}],cover:{url:'https://images.example/cover.png'},covers:[{id:'1',url:'https://images.example/cover.png',sexual:'safe',width:60,height:90,vote_count:2}],intros:[{lang:'en',value:'English'},{lang:'zh-Hans',value:'合成简介'}],companies:[{attribution_role:'developer',display_name:'Studio'}],tags:[{display_name:'标签',spoiler:'none'},{display_name:'隐藏',tier:'hidden'}],ratings:[{source:'vndb',score:8.5,vote_count:7},{source:'unknown',score:2}],characters:[{id:'11',display_name:'角色',voices:[{display_name:'声优'}],roster_role:'main',spoiler:'none'}],refs:[{source:'vndb',external_id:'v86'},{source:'steam',external_id:'10'}],claim:{site:'kungal',state:'live',site_work_id:'2'},release_date:'2020-01-02',release_date_precision:'day',content_rating:'safe'})
const response = (body,status=200,headers={})=>({body,status,headers})
const list = items=>({object:'list',items})
const hit=id=>({object:'search_result',target_object:'work',id})
const cases=[]
const add=(name,operation,responses,input={})=>cases.push({name,operation,input,responses})
add('detail-rich','details',[response(work())])
add('detail-large-string-id','details',[response(work('18446744073709551615'))],{id:'18446744073709551615'})
add('detail-partial-unknown','details',[response({object:'work',id:'86',unexpected:{a:true},characters:null,tags:null})])
add('detail-numeric-id-rejected','details',[response({...work(),id:86})])
add('detail-explicit-filter','details',[response({...work(),cover:{url:'https://images.example/explicit.png',sexual:'explicit'},covers:[{id:'1',url:'https://images.example/safe.png',sexual:'safe'}]})],{includeExplicit:false})
add('search-mapping-reordered-duplicate','search',[response({...list([hit('86'),hit('90'),hit('86')]),next_cursor:'cur_next'}),response(list([work('90'),work('999'),work('86')]))],{query:' 合成 & A ',cursor:'cur_prior'})
add('search-missing-hit','search',[response(list([hit('86'),hit('90')])),response(list([work('90')]))],{query:'合成'})
add('search-empty','search',[response(list([]))],{query:'none'})
add('search-malformed-cursor','search',[response({...list([]),next_cursor:'bad'})],{query:'none'})
for(const status of [400,401,403,404,408,429,500,502,503,504,301,307]) add('status-'+status,'details',Array.from({length:3},()=>response({code:status===429?'RATE_LIMITED':status===401?'INVALID_API_KEY':'INTERNAL_ERROR'},status)))
add('retry-after','details',[response({code:'RATE_LIMITED'},429,{'retry-after':'2'}),response(work())])
add('quota-no-retry','details',[response({code:'QUOTA_EXCEEDED'},429)])
add('shared-retries-search','search',[response({code:'SERVICE_UNAVAILABLE'},503),response(list([hit('86')])),response({code:'SERVICE_UNAVAILABLE'},503),response(list([work()]))],{query:'synthetic'})
add('merge-canonical','details',[response({object:'work',code:'ENTITY_MERGED',current_id:'90'},404),response(work('90'))])
add('merge-loop','details',[response({object:'work',code:'ENTITY_MERGED',current_id:'86'},404)])
add('merge-depth','details',['90','91','92','93'].map(current_id=>response({object:'work',code:'ENTITY_MERGED',current_id},404)))
add('merge-invalid','details',[response({object:'work',code:'ENTITY_MERGED',current_id:'bad'},404)])
add('malformed','details',[{status:200,raw:'{bad'}])
add('empty-204','details',[{status:204}])
add('oversized','details',[{status:200,oversized:true}])
add('connection-drop','details',[{drop:true}])
for(const c of cases){
 const calls=[],delays=[];let index=0
 const client=new NextMoeClient({baseUrl:'https://api.nextmoe.dev/v2/',apiKey:'synthetic-only',includeNsfw:c.input.includeExplicit??true},{fetch:async(input,init)=>{
  const url=new URL(input);calls.push({path:url.pathname,query:Object.fromEntries(url.searchParams),authorizationPresent:!!init.headers.Authorization})
  const r=c.responses[index++];if(!r||r.drop)throw Error('synthetic drop')
  return new Response(r.status===204?null:r.oversized?'x'.repeat(8*1024*1024+1):r.raw??JSON.stringify(r.body),{status:r.status,headers:{'content-type':'application/json',...r.headers}})
 },delay:async(ms)=>delays.push(ms)})
 try{c.expected={value:c.operation==='search'?await client.searchWorks(c.input.query,c.input.cursor):await client.getWork(c.input.id??'86')}}catch(e){c.expected={error:e.code}}
 c.calls=calls;c.delays=delays
}
const source=await fs.readFile(new URL('../../electron/nextmoe.ts',import.meta.url))
await fs.writeFile(new URL('../fixtures/online-oracle.json',import.meta.url),JSON.stringify({sourceSha256:createHash('sha256').update(source).digest('hex'),fixed,cases},null,2)+'\n')
console.log(JSON.stringify({cases:cases.length,sourceSha256:createHash('sha256').update(source).digest('hex')}))
