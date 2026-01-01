import { bootstrapApplication } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';

import { App } from './app/app';
import { routes } from './app/app.routes';
import { apiConfigProvider } from './app/core/api/api.config';
import { httpAuthInterceptor } from './app/core/api/http-auth.interceptor';

bootstrapApplication(App, {
  providers: [
    provideRouter(routes),
    provideHttpClient(withInterceptors([httpAuthInterceptor])),
    apiConfigProvider,
  ],
}).catch(console.error);
