import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { API_CONFIG } from '../api/api.config';
import { AuthResponseDto, LoginRequestDto, RegisterRequestDto, MeDto } from './models';
import { TokenStorage } from './token.storage';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly cfg = inject(API_CONFIG);

  login(req: LoginRequestDto) {
    return this.http.post<AuthResponseDto>(`${this.cfg.baseUrl}/auth/login`, req);
  }

  register(req: RegisterRequestDto) {
    return this.http.post<AuthResponseDto>(`${this.cfg.baseUrl}/auth/register`, req);
  }

  me() {
    return this.http.get<MeDto>(`${this.cfg.baseUrl}/auth/me`);
  }

  isAuthenticated(): boolean {
    return !!TokenStorage.get();
  }

  setToken(token: string) {
    TokenStorage.set(token);
  }

  logout() {
    TokenStorage.clear();
  }
}
