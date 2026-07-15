export class ApiError extends Error{constructor(public status:number,public detail:string){super(detail)}}
export async function getJson<T>(path:string):Promise<T>{const response=await fetch(path);if(!response.ok){const body=await response.json().catch(()=>({detail:'Unable to load this surface.'}));throw new ApiError(response.status,body.detail)}return response.json()}
