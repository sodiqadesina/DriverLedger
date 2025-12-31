export type AuthResponseDto = { token: string };

export type LoginRequestDto = {
  email: string;
  password: string;
};

export type RegisterRequestDto = {
  email: string;
  password: string;
};

export type MeDto = {
  userId: string;
  email: string;
  tenantId: string;
  roles: string[];
};
