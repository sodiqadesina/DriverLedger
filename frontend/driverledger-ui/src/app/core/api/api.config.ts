import { InjectionToken } from '@angular/core';
import { environment } from '../../../environments/environment';

export type ApiConfig = { baseUrl: string };

export const API_CONFIG = new InjectionToken<ApiConfig>('API_CONFIG');

export const apiConfigProvider = {
  provide: API_CONFIG,
  useValue: { baseUrl: environment.apiBaseUrl } satisfies ApiConfig,
};
