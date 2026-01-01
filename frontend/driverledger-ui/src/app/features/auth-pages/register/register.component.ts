import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'dl-register',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, RouterModule],
  template: `
    <div class="min-h-screen flex items-center justify-center p-4">
      <div class="w-full max-w-sm border rounded-xl p-6 bg-white">
        <h1 class="text-xl font-semibold">Create account</h1>
        <p class="text-sm opacity-70 mb-4">Register to start tracking receipts and ledger.</p>

        <form [formGroup]="form" (ngSubmit)="submit()" class="flex flex-col gap-3">
          <input class="border rounded p-2" placeholder="Email" formControlName="email" />
          <input class="border rounded p-2" placeholder="Password" type="password" formControlName="password" />

          <button class="border rounded p-2 font-semibold" [disabled]="form.invalid || loading">
            {{ loading ? 'Creating...' : 'Register' }}
          </button>

          <div class="text-sm">
            Already have an account?
            <a class="underline" routerLink="/login">Login</a>
          </div>

          <div *ngIf="error" class="text-sm border rounded p-2">
            {{ error }}
          </div>
        </form>
      </div>
    </div>
  `,
})
export class RegisterComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  loading = false;
  error: string | null = null;

  form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  submit() {
    if (this.form.invalid) return;
    this.loading = true;
    this.error = null;

    this.auth.register(this.form.getRawValue() as any).subscribe({
      next: (res) => {
        this.auth.setToken(res.token);
        this.router.navigateByUrl('/welcome');
      },
      error: (e) => {
        this.error = e?.error?.message ?? 'Registration failed.';
        this.loading = false;
      },
      complete: () => (this.loading = false),
    });
  }
}
