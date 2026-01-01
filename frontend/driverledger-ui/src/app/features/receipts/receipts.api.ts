import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { API_CONFIG } from '../../core/api/api.config';
import {
  ReceiptListItemDto,
  CreateReceiptRequestDto,
  CreateReceiptResponseDto,
  SubmitReceiptResponseDto,
  ResolveHoldRequestDto,
  ResolveHoldResponseDto
} from '../../shared/models/receipts.models';
import { UploadFileResponseDto } from '../../shared/models/files.models';

@Injectable({ providedIn: 'root' })
export class ReceiptsApi {
  private readonly http = inject(HttpClient);
  private readonly cfg = inject(API_CONFIG);

  uploadFile(file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<UploadFileResponseDto>(`${this.cfg.baseUrl}/files`, form);
  }

  list(status?: string) {
    const url = status
      ? `${this.cfg.baseUrl}/receipts?status=${encodeURIComponent(status)}`
      : `${this.cfg.baseUrl}/receipts`;
    return this.http.get<ReceiptListItemDto[]>(url);
  }

  createReceipt(req: CreateReceiptRequestDto) {
    return this.http.post<CreateReceiptResponseDto>(`${this.cfg.baseUrl}/receipts`, req);
  }

  submitReceipt(receiptId: string) {
    return this.http.post<SubmitReceiptResponseDto>(
      `${this.cfg.baseUrl}/receipts/${receiptId}/submit`,
      {}
    );
  }

  resolveHold(receiptId: string, req: ResolveHoldRequestDto) {
    return this.http.post<ResolveHoldResponseDto>(
      `${this.cfg.baseUrl}/receipts/${receiptId}/review/resolve`,
      req
    );
  }
}
