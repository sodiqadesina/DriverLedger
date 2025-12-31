import { HttpInterceptorFn } from '@angular/common/http';
import { TokenStorage } from '../auth/token.storage';

export const httpAuthInterceptor: HttpInterceptorFn = (req, next) => {
  // Skip attaching for auth endpoints (optional but clean)
  const isAuthEndpoint =
    req.url.includes('/auth/login') || req.url.includes('/auth/register');

  const token = TokenStorage.get();
  if (!token || isAuthEndpoint) return next(req);

  return next(
    req.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    })
  );
};
