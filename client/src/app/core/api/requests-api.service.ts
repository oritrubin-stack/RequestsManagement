import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  BulkUpdateItem,
  BulkUpdateResponse,
  PagedResult,
  RequestDto,
  RequestQuery,
  RequestStatus,
  RequestSummary,
  StatusChange
} from '../models/request.models';

// The only place that talks to the requests API. Filtering, sorting and paging are always done by the server.
// The base URL is relative; in development the Angular dev server proxies /api to the API (proxy.conf.json).
@Injectable({ providedIn: 'root' })
export class RequestsApiService {
  static readonly changedByHeader = 'X-User-Name';

  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/requests';

  search(query: RequestQuery): Observable<PagedResult<RequestDto>> {
    let params = new HttpParams()
      .set('sortBy', query.sortBy)
      .set('sortDirection', query.sortDirection)
      .set('page', query.page)
      .set('pageSize', query.pageSize);

    const optional: Partial<Record<keyof RequestQuery, string | undefined>> = {
      search: query.search?.trim(),
      status: query.status,
      priority: query.priority,
      organizationName: query.organizationName,
      assignedTo: query.assignedTo,
      createdFrom: query.createdFrom,
      createdTo: query.createdTo
    };

    for (const [name, value] of Object.entries(optional)) {
      if (value) {
        params = params.set(name, value);
      }
    }

    return this.http.get<PagedResult<RequestDto>>(this.baseUrl, { params });
  }

  getSummary(): Observable<RequestSummary> {
    return this.http.get<RequestSummary>(`${this.baseUrl}/summary`);
  }

  getById(id: string): Observable<RequestDto> {
    return this.http.get<RequestDto>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  getHistory(id: string): Observable<StatusChange[]> {
    return this.http.get<StatusChange[]>(`${this.baseUrl}/${encodeURIComponent(id)}/history`);
  }

  updateStatus(id: string, status: RequestStatus, expectedVersion: number, changedBy: string): Observable<RequestDto> {
    return this.http.put<RequestDto>(
      `${this.baseUrl}/${encodeURIComponent(id)}/status`,
      { status, expectedVersion },
      { headers: this.changedByHeaders(changedBy) });
  }

  bulkUpdateStatus(items: BulkUpdateItem[], changedBy: string): Observable<BulkUpdateResponse> {
    return this.http.post<BulkUpdateResponse>(
      `${this.baseUrl}/status/bulk`,
      { items },
      { headers: this.changedByHeaders(changedBy) });
  }

  private changedByHeaders(changedBy: string): HttpHeaders {
    return new HttpHeaders({ [RequestsApiService.changedByHeader]: changedBy });
  }
}
